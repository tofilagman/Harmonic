﻿using Harmonic.Buffers;
using Harmonic.Networking.Amf.Attributes;
using Harmonic.Networking.Amf.Common;
using Harmonic.Networking.Rtmp.Serialization;
using Harmonic.Networking.Utils;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;

namespace Harmonic.Networking.Amf.Serialization.Amf0
{
    public class Amf0Writer
    {
        private delegate bool GetBytesHandler<T>(T value);
        private delegate bool GetBytesHandler(object value);
        private List<object> _referenceTable = new List<object>();
        private IReadOnlyDictionary<Type, GetBytesHandler> _getBytesHandlers = null;
        private UnlimitedBuffer _writeBuffer = new UnlimitedBuffer();
        private ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;
        public int MessageLength => _writeBuffer.BufferLength;

        public Amf0Writer()
        {
            var getBytesHandlers = new Dictionary<Type, GetBytesHandler>();
            getBytesHandlers[typeof(double)] = GetBytesWrapper<double>(TryGetBytes);
            getBytesHandlers[typeof(int)] = GetBytesWrapper<double>(TryGetBytes);
            getBytesHandlers[typeof(short)] = GetBytesWrapper<double>(TryGetBytes);
            getBytesHandlers[typeof(long)] = GetBytesWrapper<double>(TryGetBytes);
            getBytesHandlers[typeof(uint)] = GetBytesWrapper<double>(TryGetBytes);
            getBytesHandlers[typeof(ushort)] = GetBytesWrapper<double>(TryGetBytes);
            getBytesHandlers[typeof(ulong)] = GetBytesWrapper<double>(TryGetBytes);
            getBytesHandlers[typeof(float)] = GetBytesWrapper<double>(TryGetBytes);
            getBytesHandlers[typeof(DateTime)] = GetBytesWrapper<DateTime>(TryGetBytes);
            getBytesHandlers[typeof(string)] = GetBytesWrapper<string>(TryGetBytes);
            getBytesHandlers[typeof(XmlDocument)] = GetBytesWrapper<XmlDocument>(TryGetBytes);
            getBytesHandlers[typeof(Unsupported)] = GetBytesWrapper<Unsupported>(TryGetBytes);
            getBytesHandlers[typeof(Undefined)] = GetBytesWrapper<Undefined>(TryGetBytes);
            getBytesHandlers[typeof(bool)] = GetBytesWrapper<bool>(TryGetBytes);
            getBytesHandlers[typeof(object)] = GetBytesWrapper<object>(TryGetBytes);
            getBytesHandlers[typeof(List<object>)] = GetBytesWrapper<List<object>>(TryGetBytes);
            _getBytesHandlers = getBytesHandlers;
        }


        private GetBytesHandler GetBytesWrapper<T>(GetBytesHandler<T> handler)
        {
            return (object v) =>
            {
                if (v is T tv)
                {
                    return handler(tv);
                }
                else
                {
                    return handler((T)Convert.ChangeType(v, typeof(T)));
                }
            };
        }

        public void GetMessage(Span<byte> buffer)
        {
            _referenceTable.Clear();
            _writeBuffer.TakeOutMemory(buffer);
        }

        public bool TryGetAvmPlusBytes()
        {
            _writeBuffer.WriteToBuffer((byte)Amf0Type.AvmPlusObject);
            return true;
        }

        private bool TryGetStringBytesImpl(string str, out bool isLongString, bool marker = false, bool forceLongString = false)
        {
            var bytesNeed = 0;
            var headerLength = 0;
            var bodyLength = 0;

            bodyLength = Encoding.UTF8.GetByteCount(str);
            bytesNeed += bodyLength;

            if (bodyLength > ushort.MaxValue || forceLongString)
            {
                headerLength = Amf0CommonValues.LONG_STRING_HEADER_LENGTH;
                isLongString = true;
                if (marker)
                {
                    _writeBuffer.WriteToBuffer((byte)Amf0Type.LongString);
                }

            }
            else
            {
                isLongString = false;
                headerLength = Amf0CommonValues.STRING_HEADER_LENGTH;
                if (marker)
                {
                    _writeBuffer.WriteToBuffer((byte)Amf0Type.String);
                }
            }
            bytesNeed += headerLength;
            var bufferBackend = _arrayPool.Rent(bytesNeed);
            try
            {
                var buffer = bufferBackend.AsSpan(0, bytesNeed);
                if (isLongString)
                {
                    if (!NetworkBitConverter.TryGetBytes((uint)bodyLength, buffer))
                    {
                        return false;
                    }
                }
                else
                {
                    if (!NetworkBitConverter.TryGetBytes((ushort)bodyLength, buffer))
                    {
                        return false;
                    }
                }

                Encoding.UTF8.GetBytes(str, buffer.Slice(headerLength));

                _writeBuffer.WriteToBuffer(buffer);
            }
            finally
            {
                _arrayPool.Return(bufferBackend);
            }

            return true;
        }

        public bool TryGetBytes(string str)
        {
            var bytesNeed = Amf0CommonValues.MARKER_LENGTH;

            var refIndex = _referenceTable.IndexOf(str);

            if (refIndex != -1)
            {
                return TryGetReferenceIndexBytes((ushort)refIndex);
            }

            if (!TryGetStringBytesImpl(str, out var isLongString, true))
            {
                return false;
            }
            _referenceTable.Add(str);
            return true;
        }

        public bool TryGetBytes(double val)
        {
            var bytesNeed = Amf0CommonValues.MARKER_LENGTH + sizeof(double);
            var bufferBackend = _arrayPool.Rent(bytesNeed);
            try
            {
                var buffer = bufferBackend.AsSpan(0, bytesNeed);
                buffer[0] = (byte)Amf0Type.Number;
                var ret = NetworkBitConverter.TryGetBytes(val, buffer.Slice(Amf0CommonValues.MARKER_LENGTH));
                _writeBuffer.WriteToBuffer(buffer);
                return ret;
            }
            finally
            {
                _arrayPool.Return(bufferBackend);
            }
        }

        public bool TryGetBytes(bool val)
        {
            var bytesNeed = Amf0CommonValues.MARKER_LENGTH + sizeof(byte);

            _writeBuffer.WriteToBuffer((byte)Amf0Type.Boolean);
            _writeBuffer.WriteToBuffer((byte)(val ? 1 : 0));

            return true;

        }

        public bool TryGetBytes(Undefined value)
        {
            var bytesNeed = Amf0CommonValues.MARKER_LENGTH;
            var bufferBackend = _arrayPool.Rent(bytesNeed);

            _writeBuffer.WriteToBuffer((byte)Amf0Type.Undefined);
            return true;

        }

        public bool TryGetBytes(Unsupported value)
        {
            var bytesNeed = Amf0CommonValues.MARKER_LENGTH;
            _writeBuffer.WriteToBuffer((byte)Amf0Type.Unsupported);

            return true;
        }

        private bool TryGetReferenceIndexBytes(ushort index)
        {
            var bytesNeed = Amf0CommonValues.MARKER_LENGTH + sizeof(ushort);
            var backend = _arrayPool.Rent(bytesNeed);
            try
            {
                var buffer = backend.AsSpan(0, bytesNeed);
                buffer[0] = (byte)Amf0Type.Reference;
                var ret = NetworkBitConverter.TryGetBytes(index, buffer.Slice(Amf0CommonValues.MARKER_LENGTH));
                _writeBuffer.WriteToBuffer(buffer);
                return ret;
            }
            finally
            {
                _arrayPool.Return(backend);
            }

        }

        public bool TryGetObjectEndBytes()
        {
            var bytesNeed = Amf0CommonValues.MARKER_LENGTH;
            _writeBuffer.WriteToBuffer((byte)Amf0Type.ObjectEnd);
            return true;
        }

        public bool TryGetBytes(DateTime dateTime)
        {
            var bytesNeed = Amf0CommonValues.MARKER_LENGTH + sizeof(double) + sizeof(short);

            var backend = _arrayPool.Rent(bytesNeed);
            try
            {
                var buffer = backend.AsSpan(0, bytesNeed);
                buffer.Slice(0, bytesNeed).Clear();
                buffer[0] = (byte)Amf0Type.Date;
                var dof = new DateTimeOffset(dateTime);
                var timestamp = (double)dof.ToUnixTimeMilliseconds();
                if (!NetworkBitConverter.TryGetBytes(timestamp, buffer.Slice(Amf0CommonValues.MARKER_LENGTH)))
                {
                    return false;
                }
                _writeBuffer.WriteToBuffer(buffer);
                return true;
            }
            finally
            {
                _arrayPool.Return(backend);
            }

        }

        public bool TryGetBytes(XmlDocument xml)
        {
            string content = null;
            using (var stringWriter = new StringWriter())
            using (var xmlTextWriter = XmlWriter.Create(stringWriter))
            {
                xml.WriteTo(xmlTextWriter);
                xmlTextWriter.Flush();
                content = stringWriter.GetStringBuilder().ToString();
            }

            if (content == null)
            {
                return false;
            }

            _writeBuffer.WriteToBuffer((byte)Amf0Type.XmlDocument);
            TryGetStringBytesImpl(content, out _, forceLongString: true);
            return true;

        }

        public bool TryGetNullBytes()
        {
            _writeBuffer.WriteToBuffer((byte)Amf0Type.Null);

            return true;
        }

        public bool TryGetValueBytes(object value)
        {
            var valueType = value != null ? value.GetType() : typeof(object);
            if (!_getBytesHandlers.TryGetValue(valueType, out var handler))
            {
                return false;
            }

            return handler(value);
        }

        // strict array
        public bool TryGetBytes(List<object> value)
        {
            if (value == null)
            {
                return TryGetNullBytes();
            }

            var bytesNeed = Amf0CommonValues.MARKER_LENGTH + sizeof(uint);

            var refIndex = _referenceTable.IndexOf(value);

            if (refIndex >= 0)
            {
                return TryGetReferenceIndexBytes((ushort)refIndex);
            }

            _writeBuffer.WriteToBuffer((byte)Amf0Type.StrictArray);
            var countBuffer = _arrayPool.Rent(sizeof(uint));
            try
            {
                if (!NetworkBitConverter.TryGetBytes((uint)value.Count, countBuffer))
                {
                    return false;
                }
                _writeBuffer.WriteToBuffer(countBuffer.AsSpan(0, sizeof(uint)));
            }
            finally
            {
                _arrayPool.Return(countBuffer);
            }

            foreach (var data in value)
            {
                if (!TryGetValueBytes(data))
                {
                    return false;
                }
            }
            _referenceTable.Add(value);
            return true;
        }

        public bool TryGetBytes(Dictionary<string, object> value)
        {
            if (value == null)
            {
                return TryGetNullBytes();
            }

            var refIndex = _referenceTable.IndexOf(value);

            if (refIndex >= 0)
            {
                return TryGetReferenceIndexBytes((ushort)refIndex);
            }
            _writeBuffer.WriteToBuffer((byte)Amf0Type.EcmaArray);
            var countBuffer = _arrayPool.Rent(sizeof(uint));
            try
            {
                if (!NetworkBitConverter.TryGetBytes((uint)value.Count, countBuffer))
                {
                    return false;
                }
                _writeBuffer.WriteToBuffer(countBuffer.AsSpan(0, sizeof(uint)));
            }
            finally
            {
                _arrayPool.Return(countBuffer);
            }

            foreach ((var key, var data) in value)
            {
                if (!TryGetStringBytesImpl(key, out _))
                {
                    return false;
                }
                if (!TryGetValueBytes(data))
                {
                    return false;
                }
            }
            if (!TryGetStringBytesImpl("", out _))
            {
                return false;
            }
            if (!TryGetObjectEndBytes())
            {
                return false;
            }
            _referenceTable.Add(value);
            return true;
        }

        public bool TryGetTypedBytes(object value)
        {
            if (value == null)
            {
                return TryGetNullBytes();
            }
            var refIndex = _referenceTable.IndexOf(value);

            if (refIndex >= 0)
            {
                return TryGetReferenceIndexBytes((ushort)refIndex);
            }
            _writeBuffer.WriteToBuffer((byte)Amf0Type.TypedObject);

            var valueType = value.GetType();
            var className = valueType.Name;

            var clsAttr = (TypedObjectAttribute)Attribute.GetCustomAttribute(valueType, typeof(TypedObjectAttribute));
            if (clsAttr != null && clsAttr.Name != null)
            {
                className = clsAttr.Name;
            }

            if (!TryGetStringBytesImpl(className, out _))
            {
                return false;
            }

            var props = valueType.GetProperties();
            
            foreach (var prop in props)
            {
                var attr = (ClassFieldAttribute)Attribute.GetCustomAttribute(prop, typeof(ClassFieldAttribute));
                if (attr != null)
                {
                    if (!TryGetStringBytesImpl(attr.Name ?? prop.Name, out _))
                    {
                        return false;
                    }
                    if (!TryGetValueBytes(prop.GetValue(value)))
                    {
                        return false;
                    }
                }
            }

            if (!TryGetStringBytesImpl("", out _))
            {
                return false;
            }
            if (!TryGetObjectEndBytes())
            {
                return false;
            }
            _referenceTable.Add(value);
            return true;
        }

        public bool TryGetBytes(object value)
        {
            if (value == null)
            {
                return TryGetNullBytes();
            }
            var refIndex = _referenceTable.IndexOf(value);

            if (refIndex >= 0)
            {
                return TryGetReferenceIndexBytes((ushort)refIndex);
            }
            _writeBuffer.WriteToBuffer((byte)Amf0Type.Object);

            if (!TryGetObjectBytesImpl(value))
            {
                return false;
            }

            if (!TryGetStringBytesImpl("", out _))
            {
                return false;
            }
            if (!TryGetObjectEndBytes())
            {
                return false;
            }
            _referenceTable.Add(value);
            return true;
        }

        private bool TryGetObjectBytesImpl(object value)
        {
            var props = value.GetType().GetProperties();
            foreach (var prop in props)
            {
                var propValue = prop.GetValue(value);
                if (!TryGetStringBytesImpl(prop.Name, out _))
                {
                    return false;
                }
                if (!TryGetValueBytes(propValue))
                {
                    if (!TryGetObjectBytesImpl(propValue))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}