﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Iot.Device.SocketCan
{
    /// <summary>
    /// Allows reading and writing raw frames to CAN Bus
    /// </summary>
    public class CanRaw : IDisposable
    {
        private Socket _socket;

        /// <summary>
        /// Constructs CanRaw instance
        /// </summary>
        /// <param name="networkInterface">Name of the network interface</param>
        public CanRaw(string networkInterface = "can0")
        {
            _socket = new Socket(AddressFamily.ControllerAreaNetwork, SocketType.Raw, ProtocolType.Raw);

            var endpoint = new CanEndPoint(_socket, "vcan0");
            _socket.Bind(endpoint);
        }

        /// <summary>
        /// Writes frame to the CAN Bus
        /// </summary>
        /// <param name="data">Data to write (at most 8 bytes)</param>
        /// <param name="id">Recipient identifier</param>
        /// <remarks><paramref name="id"/> can be ignored by recipient - anyone connected to the bus can read or write any frames</remarks>
        public void WriteFrame(ReadOnlySpan<byte> data, CanId id)
        {
            if (!id.IsValid)
            {
                throw new ArgumentException(nameof(id), "Id is not valid. Ensure Error flag is not set and that id is in the valid range (11-bit for standard frame and 29-bit for extended frame).");
            }

            if (data.Length > CanFrame.MaxLength)
            {
                throw new ArgumentException(nameof(data), $"Data length cannot exceed {CanFrame.MaxLength} bytes.");
            }

            CanFrame frame = new CanFrame();
            frame.Id = id;
            frame.Length = (byte)data.Length;
            Debug.Assert(frame.IsValid, "Frame is not valid");

            unsafe
            {
                Span<byte> frameData = new Span<byte>(frame.Data, data.Length);
                data.CopyTo(frameData);
            }

            ReadOnlySpan<CanFrame> frameSpan = MemoryMarshal.CreateReadOnlySpan(ref frame, 1);
            ReadOnlySpan<byte> buff = MemoryMarshal.AsBytes(frameSpan);
            try
            {
                _socket.Send(buff);
            }
            catch (SocketException ex)
            {
                throw new IOException("Socket write operation failed.", ex);
            }
        }

        /// <summary>
        /// Reads frame from the bus
        /// </summary>
        /// <param name="data">Data where output data should be written to</param>
        /// <param name="frameLength">Length of the data read</param>
        /// <param name="id">Recipient identifier</param>
        /// <returns></returns>
        public bool TryReadFrame(Span<byte> data, out int frameLength, out CanId id)
        {
            if (data.Length < CanFrame.MaxLength)
            {
                throw new ArgumentException(nameof(data), $"Value must be a minimum of {CanFrame.MaxLength} bytes.");
            }

            CanFrame frame = new CanFrame();

            Span<CanFrame> frameSpan = MemoryMarshal.CreateSpan(ref frame, 1);
            Span<byte> buff = MemoryMarshal.AsBytes(frameSpan);
            try
            {
                while (buff.Length > 0)
                {
                    int read = _socket.Receive(buff);
                    buff = buff.Slice(read);
                }
            }
            catch (SocketException ex)
            {
                if ((ex.SocketErrorCode == SocketError.WouldBlock) && !_socket.Blocking)
                {
                    id = frame.Id;
                    frameLength = 0;
                    return false;
                }

                throw new IOException("Socket read operation failed.", ex);
            }

            id = frame.Id;
            frameLength = frame.Length;

            if (!frame.IsValid)
            {
                // invalid frame
                // we will leave id filled in case it is useful for anyone
                frameLength = 0;
                return false;
            }

            // This is guaranteed by minimum buffer length and the fact that frame is valid
            Debug.Assert(frame.Length <= data.Length, "Invalid frame length");

            unsafe
            {
                // We should not use input buffer directly for reading:
                // - we do not know how many bytes will be read up front without reading length first
                // - we should not write anything more than pointed by frameLength
                // - we still need to read the remaining bytes to read the full frame
                // Considering there are at most 8 bytes to read it is cheaper
                // to copy rather than doing multiple syscalls.
                Span<byte> frameData = new Span<byte>(frame.Data, frame.Length);
                frameData.CopyTo(data);
            }

            return true;
        }

        /// <summary>
        /// Set filter on the bus to read only from specified recipient.
        /// </summary>
        /// <param name="id">Recipient identifier</param>
        public void Filter(CanId id)
        {
            if (!id.IsValid)
            {
                throw new ArgumentException(nameof(id), "Value must be a valid CanId");
            }

            Span<Interop.CanFilter> filters = stackalloc Interop.CanFilter[1];
            filters[0].can_id = id.Raw;
            filters[0].can_mask = id.Value | (uint)CanFlags.ExtendedFrameFormat | (uint)CanFlags.RemoteTransmissionRequest;

            Interop.SetCanRawSocketOption<Interop.CanFilter>(_socket, Interop.CanSocketOption.CAN_RAW_FILTER, filters);
        }

        private static bool IsEff(uint address)
        {
            // has explicit flag or address does not fit in SFF addressing mode
            return (address & (uint)CanFlags.ExtendedFrameFormat) != 0
                || (address & Interop.CAN_EFF_MASK) != (address & Interop.CAN_SFF_MASK);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _socket.Dispose();
        }

        /// <summary>
        /// Enables or disables blocking operation
        /// </summary>
        public bool Blocking
        {
            get => _socket.Blocking;
            set => _socket.Blocking = value;
        }
    }
}
