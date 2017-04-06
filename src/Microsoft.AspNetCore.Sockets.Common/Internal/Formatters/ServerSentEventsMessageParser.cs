﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.using System;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace Microsoft.AspNetCore.Sockets.Internal.Formatters
{
    public class ServerSentEventsMessageParser
    {
        const byte ByteCR = (byte)'\r';
        const byte ByteLF = (byte)'\n';
        const byte ByteSpace = (byte)' ';

        private InternalParseState _internalParserState = InternalParseState.ReadMessageType;
        private IList<byte[]> _data = new List<byte[]>();

        public ParseResult ParseMessage(ReadableBuffer buffer, out ReadCursor consumed, out ReadCursor examined, out Message message)
        {
            consumed = buffer.Start;
            examined = buffer.End;
            message = new Message();
            var messageType = MessageType.Text;
            var reader = new ReadableBufferReader(buffer);

            var start = consumed;
            var end = examined;

            while (!reader.End)
            {
                if (ReadCursorOperations.Seek(start, end, out var lineEnd, ByteLF) == -1)
                {
                    if(end != buffer.End)
                    {
                        return ParseResult.Incomplete;
                    }
                    throw new FormatException("Expected a '\r\n' line ending");
                }

                lineEnd = buffer.Move(lineEnd, 1);
                var line = ConvertBufferToSpan(buffer.Slice(start, lineEnd));
                reader.Skip(line.Length);

                if(!(line.Length > 1))
                {
                    throw new FormatException("Error parsing data from event stream");
                }

                //Strip the \r\n from the span
                line = line.Slice(0, line.Length - 2);

                switch (_internalParserState)
                {
                    case InternalParseState.ReadMessageType:
                        messageType = GetMessageType(line);
                        start = lineEnd;
                        _internalParserState = InternalParseState.ReadMessagePayload;
                        consumed = lineEnd;

                        //Peek into next byte. If it is a carriage return byte, then advance to the next state
                        if (reader.Peek() == ByteCR)
                        {
                            _internalParserState = InternalParseState.ReadEndOfMessage;
                        }
                        break;
                    case InternalParseState.ReadMessagePayload:
                        //Slice away the 'data: '
                        var newData = line.Slice(line.IndexOf(ByteSpace) + 1).ToArray();
                        start = lineEnd;
                        _data.Add(newData);

                        //Peek into next byte. If it is a carriage return byte, then advance to the next state
                        if (reader.Peek() == ByteCR)
                        {
                            _internalParserState = InternalParseState.ReadEndOfMessage;
                        }

                        consumed = lineEnd;
                        break;
                    case InternalParseState.ReadEndOfMessage:
                        if (ReadCursorOperations.Seek(start, end, out lineEnd, ByteLF) == -1)
                        {
                            // The message has ended with \r\n\r
                            return ParseResult.Incomplete;
                        }

                        //To check for the invalid case of there being more data after the frame end \r\n\r\n
                        if (!reader.End)
                        {
                            throw new FormatException("Unexpected data after line ending");
                        }


                        if (_data.Count > 0)
                        {
                            //Find the final size of the payload
                            var payloadSize = 0;
                            foreach (var dataLine in _data)
                            {
                                payloadSize += dataLine.Length;
                            }
                            var payload = new byte[payloadSize];

                            //Copy the contents of the data array to a single buffer
                            var marker = 0;
                            foreach (var dataLine in _data)
                            {
                                dataLine.CopyTo(payload, marker);
                                marker += dataLine.Length;
                            }

                            message = new Message(payload, messageType);
                        }
                        else
                        {
                            //Empty message
                            message = new Message(new byte[0], messageType);
                        }

                        consumed = buffer.End;
                        return ParseResult.Completed;
                }
            }
            throw new FormatException("Error parsing data from event stream");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Span<byte> ConvertBufferToSpan(ReadableBuffer buffer)
        {
            if (buffer.IsSingleSpan)
            {
                return buffer.First.Span;
            }
            return buffer.ToArray();
        }

        public void Reset()
        {
            _internalParserState = InternalParseState.ReadMessageType;
            _data.Clear();
        }

        private void CheckLinePrefix(Span<byte> line)
        {
            //var prefix = "data: ".To
            //if(line.Length > 6)
            //{
            //    line.StartsWith
            //}
        }

        private MessageType GetMessageType(ReadOnlySpan<byte> line)
        {
            //Skip the "data: " part of the line
            if (line.Length != 7)
            {
                throw new FormatException("There was an error parsing the message type");
            }

            var type = (char)line[6];
            switch (type)
            {
                case 'T':
                    return MessageType.Text;
                case 'B':
                    return MessageType.Binary;
                case 'C':
                    return MessageType.Close;
                case 'E':
                    return MessageType.Error;
                default:
                    throw new FormatException($"Unknown message type: '{type}'");
            }
        }

        public enum ParseResult
        {
            Completed,
            Incomplete,
        }

        private enum InternalParseState
        {
            Initial,
            ReadMessageType,
            ReadMessagePayload,
            ReadEndOfMessage,
            Error
        }
    }
}