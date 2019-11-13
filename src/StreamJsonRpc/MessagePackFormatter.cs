﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.IO;
    using System.IO.Pipelines;
    using System.Reflection;
    using System.Runtime.Serialization;
    using MessagePack;
    using MessagePack.Formatters;
    using MessagePack.Resolvers;
    using Microsoft;
    using Nerdbank.Streams;
    using StreamJsonRpc.Protocol;
    using StreamJsonRpc.Reflection;

    /// <summary>
    /// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
    /// </summary>
    /// <remarks>
    /// The MessagePack implementation used here comes from https://github.com/neuecc/MessagePack-CSharp.
    /// The README on that project site describes use cases and its performance compared to alternative
    /// .NET MessagePack implementations and this one appears to be the best by far.
    /// </remarks>
    public class MessagePackFormatter : IJsonRpcMessageFormatter, IJsonRpcInstanceContainer, IDisposable
    {
        /// <summary>
        /// The constant "jsonrpc".
        /// </summary>
        private const string VersionPropertyName = "jsonrpc";

        private const string IdPropertyName = "id";

        private const string MethodPropertyName = "method";

        private const string ResultPropertyName = "result";

        private const string ErrorPropertyName = "error";

        /// <summary>
        /// <see cref="MessageFormatterProgressTracker"/> instance containing useful methods to help on the implementation of message formatters.
        /// </summary>
        private readonly MessageFormatterProgressTracker formatterProgressTracker = new MessageFormatterProgressTracker();

        /// <summary>
        /// The helper for marshaling pipes as RPC method arguments.
        /// </summary>
        private readonly MessageFormatterDuplexPipeTracker duplexPipeTracker = new MessageFormatterDuplexPipeTracker();

        /// <summary>
        /// The options to use for serializing top-level RPC messages.
        /// </summary>
        private readonly MessagePackSerializerOptions messageSerializationOptions;

        private readonly ProgressFormatterResolver progressFormatterResolver;

        private readonly PipeFormatterResolver pipeFormatterResolver;

        /// <summary>
        /// The options to use for serializing user data (e.g. arguments, return values and errors).
        /// </summary>
        private MessagePackSerializerOptions userDataSerializationOptions = MessagePackSerializerOptions.Standard;

        /// <summary>
        /// The formatter to use when serializing user data that we only see typed as <see cref="object"/>.
        /// </summary>
        private DynamicObjectTypeFallbackFormatter dynamicObjectTypeFormatterForUserSuppliedResolver;

        /// <summary>
        /// Backing field for the <see cref="IJsonRpcInstanceContainer.Rpc"/> property.
        /// </summary>
        private JsonRpc? rpc;

        /// <summary>
        /// Initializes a new instance of the <see cref="MessagePackFormatter"/> class.
        /// </summary>
        public MessagePackFormatter()
            : this(compress: false)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MessagePackFormatter"/> class.
        /// </summary>
        /// <param name="compress">A value indicating whether to use LZ4 compression.</param>
        private MessagePackFormatter(bool compress)
        {
            if (compress)
            {
                // Before we enable this, we need a way to ensure that the LZ4-expanded buffers stick around long enough for our deferred deserialization.
                // See https://github.com/neuecc/MessagePack-CSharp/issues/109#issuecomment-551370773
                throw new NotSupportedException();
            }

            // Set up initial options for our own message types.
            this.messageSerializationOptions = MessagePackSerializerOptions.Standard
                .WithLZ4Compression(useLZ4Compression: compress)
                .WithResolver(this.CreateTopLevelMessageResolver());

            // Create the specialized formatters/resolvers that we will inject into the chain for user data.
            this.progressFormatterResolver = new ProgressFormatterResolver(this);
            this.pipeFormatterResolver = new PipeFormatterResolver(this);

            // Set up default user data resolver.
            this.SetMessagePackSerializerOptions(StandardResolverAllowPrivate.Options);
            (this.userDataSerializationOptions, this.dynamicObjectTypeFormatterForUserSuppliedResolver) = this.MassageUserDataOptions(StandardResolverAllowPrivate.Options);
        }

        private interface IJsonRpcMessagePackRetention
        {
            /// <summary>
            /// Gets the original msgpack sequence that was deserialized into this message.
            /// </summary>
            /// <remarks>
            /// The buffer is only retained for a short time. If it has already been cleared, the result of this property is an empty sequence.
            /// </remarks>
            ReadOnlySequence<byte> OriginalMessagePack { get; }
        }

        /// <inheritdoc/>
        JsonRpc IJsonRpcInstanceContainer.Rpc
        {
            set
            {
                Verify.Operation(this.rpc == null, "This formatter already belongs to another JsonRpc instance. Create a new instance of this formatter for each new JsonRpc instance.");

                this.rpc = value;
            }
        }

        /// <summary>
        /// Gets or sets the <see cref="MultiplexingStream"/> that may be used to establish out of band communication (e.g. marshal <see cref="IDuplexPipe"/> arguments).
        /// </summary>
        public MultiplexingStream? MultiplexingStream
        {
            get => this.duplexPipeTracker.MultiplexingStream;
            set
            {
                Verify.Operation(this.rpc == null, Resources.FormatterConfigurationLockedAfterJsonRpcAssigned);
                this.duplexPipeTracker.MultiplexingStream = value;
            }
        }

        /// <summary>
        /// Sets the <see cref="MessagePackSerializerOptions"/> to use for serialization of user data.
        /// </summary>
        /// <param name="options">The options to use. Before this call, the options used come from <see cref="StandardResolverAllowPrivate.Options"/>.</param>
        public void SetMessagePackSerializerOptions(MessagePackSerializerOptions options)
        {
            Requires.NotNull(options, nameof(options));

            (this.userDataSerializationOptions, this.dynamicObjectTypeFormatterForUserSuppliedResolver) = this.MassageUserDataOptions(options);
        }

        /// <inheritdoc/>
        public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer) => MessagePackSerializer.Deserialize<JsonRpcMessage>(contentBuffer, this.messageSerializationOptions);

        /// <inheritdoc/>
        public void Serialize(IBufferWriter<byte> contentBuffer, JsonRpcMessage message)
        {
            if (message is IJsonRpcMessageWithId msgWithId && (message is Protocol.JsonRpcResult || message is Protocol.JsonRpcError))
            {
                this.duplexPipeTracker.OnResponseSent(msgWithId.RequestId, successful: msgWithId is Protocol.JsonRpcResult);
            }

            if (message is Protocol.JsonRpcRequest request && request.Arguments != null && request.ArgumentsList == null && !(request.Arguments is IReadOnlyDictionary<string, object?>))
            {
                // This request contains named arguments, but not using a standard dictionary. Convert it to a dictionary so that
                // the parameters can be matched to the method we're invoking.
                request.Arguments = GetParamsObjectDictionary(request.Arguments);
            }

            var writer = new MessagePackWriter(contentBuffer);
            MessagePackSerializer.Serialize(ref writer, message, this.messageSerializationOptions);
            writer.Flush();
        }

        /// <inheritdoc/>
        public object GetJsonText(JsonRpcMessage message) => message is IJsonRpcMessagePackRetention retainedMsgPack ? MessagePackSerializer.ConvertToJson(retainedMsgPack.OriginalMessagePack, this.messageSerializationOptions) : MessagePackSerializer.SerializeToJson(message, this.messageSerializationOptions);

        /// <inheritdoc/>
        public void Dispose()
        {
            this.duplexPipeTracker.Dispose();
        }

        /// <summary>
        /// Extracts a dictionary of property names and values from the specified params object.
        /// </summary>
        /// <param name="paramsObject">The params object.</param>
        /// <returns>A dictionary, or <c>null</c> if <paramref name="paramsObject"/> is null.</returns>
        /// <remarks>
        /// This method supports DataContractSerializer-compliant types. This includes C# anonymous types.
        /// </remarks>
        [return: NotNullIfNotNull("paramsObject")]
        private static IReadOnlyDictionary<string, object?>? GetParamsObjectDictionary(object? paramsObject)
        {
            if (paramsObject == null)
            {
                return null;
            }

            var result = new Dictionary<string, object?>(StringComparer.Ordinal);

            TypeInfo paramsTypeInfo = paramsObject.GetType().GetTypeInfo();
            bool isDataContract = paramsTypeInfo.GetCustomAttribute<DataContractAttribute>() != null;

            BindingFlags bindingFlags = BindingFlags.FlattenHierarchy | BindingFlags.Public | BindingFlags.Instance;
            if (isDataContract)
            {
                bindingFlags |= BindingFlags.NonPublic;
            }

            bool TryGetSerializationInfo(MemberInfo memberInfo, out string key)
            {
                key = memberInfo.Name;
                if (isDataContract)
                {
                    DataMemberAttribute dataMemberAttribute = memberInfo.GetCustomAttribute<DataMemberAttribute>();
                    if (dataMemberAttribute == null)
                    {
                        return false;
                    }

                    if (!dataMemberAttribute.EmitDefaultValue)
                    {
                        throw new NotSupportedException($"(DataMemberAttribute.EmitDefaultValue == false) is not supported but was found on: {memberInfo.DeclaringType.FullName}.{memberInfo.Name}.");
                    }

                    key = dataMemberAttribute.Name ?? memberInfo.Name;
                    return true;
                }
                else
                {
                    return memberInfo.GetCustomAttribute<IgnoreDataMemberAttribute>() == null;
                }
            }

            foreach (PropertyInfo property in paramsTypeInfo.GetProperties(bindingFlags))
            {
                if (property.GetMethod != null)
                {
                    if (TryGetSerializationInfo(property, out string key))
                    {
                        result[key] = property.GetValue(paramsObject);
                    }
                }
            }

            foreach (FieldInfo field in paramsTypeInfo.GetFields(bindingFlags))
            {
                if (TryGetSerializationInfo(field, out string key))
                {
                    result[key] = field.GetValue(paramsObject);
                }
            }

            return result;
        }

        private static ReadOnlySequence<byte> GetSliceForNextToken(ref MessagePackReader reader)
        {
            SequencePosition startingPosition = reader.Position;
            reader.Skip();
            SequencePosition endingPosition = reader.Position;
            return reader.Sequence.Slice(startingPosition, endingPosition);
        }

        /// <summary>
        /// Takes the user-supplied resolver for their data types and prepares the wrapping options
        /// and the dynamic object wrapper for serialization.
        /// </summary>
        /// <param name="userSuppliedOptions">The options for user data that is supplied by the user (or the default).</param>
        /// <returns>The <see cref="MessagePackSerializerOptions"/> to use for all user data (args, return values and error data) and a special formatter to use when all we have is <see cref="object"/> for this user data.</returns>
        private (MessagePackSerializerOptions UserDataOptions, DynamicObjectTypeFallbackFormatter DynamicObjectTypeFormatter) MassageUserDataOptions(MessagePackSerializerOptions userSuppliedOptions)
        {
            var formatters = new IMessagePackFormatter[]
            {
                // We preset this one in user data because $/cancellation methods can carry RequestId values as arguments.
                RequestIdFormatter.Instance,

                // We preset this one because for some protocols like IProgress<T>, tokens are passed in that we must relay exactly back to the client as an argument.
                RawMessagePackFormatter.Instance,
            };
            var resolvers = new IFormatterResolver[]
            {
                userSuppliedOptions.Resolver,

                // Add our own resolvers to fill in specialized behavior if the user doesn't provide/override it by their own resolver.
                this.progressFormatterResolver,
                this.pipeFormatterResolver,
            };
            IFormatterResolver userDataResolver = CompositeResolver.Create(formatters, resolvers);

            MessagePackSerializerOptions userDataOptions = userSuppliedOptions
                .WithLZ4Compression(false) // If/when we support LZ4 compression, it will be at the message level -- not the user-data level.
                .WithResolver(userDataResolver);

            return (userDataOptions, new DynamicObjectTypeFallbackFormatter(userDataResolver));
        }

        private IFormatterResolver CreateTopLevelMessageResolver()
        {
            var formatters = new IMessagePackFormatter[]
            {
                RequestIdFormatter.Instance,
                JsonRpcMessageFormatter.Instance,
                new JsonRpcRequestFormatter(this),
                new JsonRpcResultFormatter(this),
                new JsonRpcErrorFormatter(this),
                new JsonRpcErrorDetailFormatter(this),
            };
            var resolvers = new IFormatterResolver[]
            {
                StandardResolverAllowPrivate.Instance,
            };
            return CompositeResolver.Create(formatters, resolvers);
        }

        private struct RawMessagePack
        {
            private readonly ReadOnlySequence<byte> raw;

            private RawMessagePack(ReadOnlySequence<byte> raw)
            {
                this.raw = raw;
            }

            internal static RawMessagePack ReadRaw(ref MessagePackReader reader)
            {
                SequencePosition initialPosition = reader.Position;
                reader.Skip();
                return new RawMessagePack(reader.Sequence.Slice(initialPosition, reader.Position));
            }

            internal void WriteRaw(ref MessagePackWriter writer) => writer.WriteRaw(this.raw);
        }

        private class RequestIdFormatter : IMessagePackFormatter<RequestId>
        {
            internal static readonly RequestIdFormatter Instance = new RequestIdFormatter();

            private RequestIdFormatter()
            {
            }

            public RequestId Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                if (reader.NextMessagePackType == MessagePackType.Integer)
                {
                    return new RequestId(reader.ReadInt64());
                }
                else
                {
                    return new RequestId(reader.ReadString());
                }
            }

            public void Serialize(ref MessagePackWriter writer, RequestId value, MessagePackSerializerOptions options)
            {
                if (value.Number.HasValue)
                {
                    writer.Write(value.Number.Value);
                }
                else
                {
                    writer.Write(value.String);
                }
            }
        }

        private class RawMessagePackFormatter : IMessagePackFormatter<RawMessagePack>
        {
            internal static readonly RawMessagePackFormatter Instance = new RawMessagePackFormatter();

            private RawMessagePackFormatter()
            {
            }

            public RawMessagePack Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                return RawMessagePack.ReadRaw(ref reader);
            }

            public void Serialize(ref MessagePackWriter writer, RawMessagePack value, MessagePackSerializerOptions options)
            {
                value.WriteRaw(ref writer);
            }
        }

        private class ProgressFormatterResolver : IFormatterResolver
        {
            private readonly MessagePackFormatter mainFormatter;

            private readonly Dictionary<Type, IMessagePackFormatter?> progressFormatters = new Dictionary<Type, IMessagePackFormatter?>();

            internal ProgressFormatterResolver(MessagePackFormatter formatter)
            {
                this.mainFormatter = formatter;
            }

            public IMessagePackFormatter<T>? GetFormatter<T>()
            {
                lock (this.progressFormatters)
                {
                    if (!this.progressFormatters.TryGetValue(typeof(T), out IMessagePackFormatter? formatter))
                    {
                        if (typeof(T).IsConstructedGenericType && typeof(T).GetGenericTypeDefinition().Equals(typeof(IProgress<>)))
                        {
                            formatter = new ProgressServerFormatter<T>(this.mainFormatter);
                        }
                        else if (MessageFormatterProgressTracker.IsSupportedProgressType(typeof(T)))
                        {
                            formatter = new ProgressClientFormatter<T>(this.mainFormatter);
                        }

                        this.progressFormatters.Add(typeof(T), formatter);
                    }

                    return (IMessagePackFormatter<T>?)formatter;
                }
            }

            private class ProgressClientFormatter<TClass> : IMessagePackFormatter<TClass>
            {
                private readonly MessagePackFormatter formatter;

                internal ProgressClientFormatter(MessagePackFormatter formatter)
                {
                    this.formatter = formatter;
                }

                [return: MaybeNull]
                public TClass Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
                {
                    throw new NotSupportedException("This formatter only serializes IProgress<T> instances.");
                }

                public void Serialize(ref MessagePackWriter writer, TClass value, MessagePackSerializerOptions options)
                {
                    // The resolver should not have selected this formatter for a null value.
                    Assumes.True(value is object);

                    long progressId = this.formatter.formatterProgressTracker.GetTokenForProgress(value);
                    writer.Write(progressId);
                }
            }

            private class ProgressServerFormatter<TClass> : IMessagePackFormatter<TClass>
            {
                private readonly MessagePackFormatter formatter;

                internal ProgressServerFormatter(MessagePackFormatter formatter)
                {
                    this.formatter = formatter;
                }

                [return: MaybeNull]
                public TClass Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
                {
                    if (reader.TryReadNil())
                    {
                        return default!;
                    }

                    Assumes.NotNull(this.formatter.rpc);
                    RawMessagePack token = RawMessagePack.ReadRaw(ref reader);
                    return (TClass)this.formatter.formatterProgressTracker.CreateProgress(this.formatter.rpc, token, typeof(TClass));
                }

                public void Serialize(ref MessagePackWriter writer, TClass value, MessagePackSerializerOptions options)
                {
                    throw new NotSupportedException("This formatter only deserializes IProgress<T> instances.");
                }
            }
        }

        private class PipeFormatterResolver : IFormatterResolver
        {
            private readonly MessagePackFormatter mainFormatter;

            private readonly Dictionary<Type, IMessagePackFormatter?> pipeFormatters = new Dictionary<Type, IMessagePackFormatter?>();

            internal PipeFormatterResolver(MessagePackFormatter formatter)
            {
                this.mainFormatter = formatter;
            }

            public IMessagePackFormatter<T>? GetFormatter<T>()
            {
                lock (this.pipeFormatters)
                {
                    if (!this.pipeFormatters.TryGetValue(typeof(T), out IMessagePackFormatter? formatter))
                    {
                        if (typeof(IDuplexPipe).IsAssignableFrom(typeof(T)))
                        {
                            formatter = (IMessagePackFormatter)Activator.CreateInstance(typeof(DuplexPipeFormatter<>).MakeGenericType(typeof(T)), this.mainFormatter);
                        }
                        else if (typeof(PipeReader).IsAssignableFrom(typeof(T)))
                        {
                            formatter = (IMessagePackFormatter)Activator.CreateInstance(typeof(PipeReaderFormatter<>).MakeGenericType(typeof(T)), this.mainFormatter);
                        }
                        else if (typeof(PipeWriter).IsAssignableFrom(typeof(T)))
                        {
                            formatter = (IMessagePackFormatter)Activator.CreateInstance(typeof(PipeWriterFormatter<>).MakeGenericType(typeof(T)), this.mainFormatter);
                        }
                        else if (typeof(Stream).IsAssignableFrom(typeof(T)))
                        {
                            formatter = (IMessagePackFormatter)Activator.CreateInstance(typeof(StreamFormatter<>).MakeGenericType(typeof(T)), this.mainFormatter);
                        }

                        this.pipeFormatters.Add(typeof(T), formatter);
                    }

                    return (IMessagePackFormatter<T>?)formatter;
                }
            }

            private class DuplexPipeFormatter<T> : IMessagePackFormatter<T?>
                where T : class, IDuplexPipe
            {
                private readonly MessagePackFormatter formatter;

                public DuplexPipeFormatter(MessagePackFormatter formatter)
                {
                    this.formatter = formatter;
                }

                public T? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
                {
                    if (reader.TryReadNil())
                    {
                        return null;
                    }

                    return (T)this.formatter.duplexPipeTracker.GetPipe(reader.ReadInt32());
                }

                public void Serialize(ref MessagePackWriter writer, T? value, MessagePackSerializerOptions options)
                {
                    if (this.formatter.duplexPipeTracker.GetToken(value) is { } token)
                    {
                        writer.Write(token);
                    }
                    else
                    {
                        writer.WriteNil();
                    }
                }
            }

            private class PipeReaderFormatter<T> : IMessagePackFormatter<T?>
                where T : PipeReader
            {
                private readonly MessagePackFormatter formatter;

                public PipeReaderFormatter(MessagePackFormatter formatter)
                {
                    this.formatter = formatter;
                }

                public T? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
                {
                    if (reader.TryReadNil())
                    {
                        return null;
                    }

                    return (T)this.formatter.duplexPipeTracker.GetPipeReader(reader.ReadInt32());
                }

                public void Serialize(ref MessagePackWriter writer, T? value, MessagePackSerializerOptions options)
                {
                    if (this.formatter.duplexPipeTracker.GetToken(value) is { } token)
                    {
                        writer.Write(token);
                    }
                    else
                    {
                        writer.WriteNil();
                    }
                }
            }

            private class PipeWriterFormatter<T> : IMessagePackFormatter<T?>
                where T : PipeWriter
            {
                private readonly MessagePackFormatter formatter;

                public PipeWriterFormatter(MessagePackFormatter formatter)
                {
                    this.formatter = formatter;
                }

                public T? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
                {
                    if (reader.TryReadNil())
                    {
                        return null;
                    }

                    return (T)this.formatter.duplexPipeTracker.GetPipeWriter(reader.ReadInt32());
                }

                public void Serialize(ref MessagePackWriter writer, T? value, MessagePackSerializerOptions options)
                {
                    if (this.formatter.duplexPipeTracker.GetToken(value) is { } token)
                    {
                        writer.Write(token);
                    }
                    else
                    {
                        writer.WriteNil();
                    }
                }
            }

            private class StreamFormatter<T> : IMessagePackFormatter<T?>
                where T : Stream
            {
                private readonly MessagePackFormatter formatter;

                public StreamFormatter(MessagePackFormatter formatter)
                {
                    this.formatter = formatter;
                }

                public T? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
                {
                    if (reader.TryReadNil())
                    {
                        return null;
                    }

                    return (T)this.formatter.duplexPipeTracker.GetPipe(reader.ReadInt32()).AsStream();
                }

                public void Serialize(ref MessagePackWriter writer, T? value, MessagePackSerializerOptions options)
                {
                    if (this.formatter.duplexPipeTracker.GetToken(value?.UsePipe()) is { } token)
                    {
                        writer.Write(token);
                    }
                    else
                    {
                        writer.WriteNil();
                    }
                }
            }
        }

        private class JsonRpcMessageFormatter : IMessagePackFormatter<JsonRpcMessage>
        {
            internal static readonly JsonRpcMessageFormatter Instance = new JsonRpcMessageFormatter();

            private JsonRpcMessageFormatter()
            {
            }

            public JsonRpcMessage Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                MessagePackReader readAhead = reader;
                int propertyCount = readAhead.ReadMapHeader();
                for (int i = 0; i < propertyCount; i++)
                {
                    string propertyName = readAhead.ReadString();
                    if (propertyName == MethodPropertyName)
                    {
                        return options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcRequest>().Deserialize(ref reader, options);
                    }
                    else if (propertyName == ResultPropertyName)
                    {
                        return options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcResult>().Deserialize(ref reader, options);
                    }
                    else if (propertyName == ErrorPropertyName)
                    {
                        return options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcError>().Deserialize(ref reader, options);
                    }

                    // Skip over the entire value of this property.
                    readAhead.Skip();
                }

                throw new UnrecognizedJsonRpcMessageException();
            }

            public void Serialize(ref MessagePackWriter writer, JsonRpcMessage value, MessagePackSerializerOptions options)
            {
                Requires.NotNull(value, nameof(value));

                switch (value)
                {
                    case Protocol.JsonRpcRequest request:
                        options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcRequest>().Serialize(ref writer, request, options);
                        break;
                    case Protocol.JsonRpcResult result:
                        options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcResult>().Serialize(ref writer, result, options);
                        break;
                    case Protocol.JsonRpcError error:
                        options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcError>().Serialize(ref writer, error, options);
                        break;
                    default:
                        throw new NotSupportedException("Unexpected JsonRpcMessage-derived type: " + value.GetType().Name);
                }
            }
        }

        private class JsonRpcRequestFormatter : IMessagePackFormatter<Protocol.JsonRpcRequest>
        {
            private const string ParamsPropertyName = "params";

            private readonly MessagePackFormatter formatter;

            internal JsonRpcRequestFormatter(MessagePackFormatter formatter)
            {
                this.formatter = formatter;
            }

            public Protocol.JsonRpcRequest Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                var result = new JsonRpcRequest(this.formatter)
                {
                    OriginalMessagePack = reader.Sequence,
                };

                int propertyCount = reader.ReadMapHeader();
                for (int propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
                {
                    switch (reader.ReadString())
                    {
                        case VersionPropertyName:
                            result.Version = reader.ReadString();
                            break;
                        case IdPropertyName:
                            result.RequestId = options.Resolver.GetFormatterWithVerify<RequestId>().Deserialize(ref reader, options);
                            break;
                        case MethodPropertyName:
                            result.Method = reader.ReadString();
                            break;
                        case ParamsPropertyName:
                            SequencePosition paramsTokenStartPosition = reader.Position;

                            // Parse out the arguments into a dictionary or array, but don't deserialize them because we don't yet know what types to deserialize them to.
                            switch (reader.NextMessagePackType)
                            {
                                case MessagePackType.Array:
                                    var positionalArgs = new ReadOnlySequence<byte>[reader.ReadArrayHeader()];
                                    for (int i = 0; i < positionalArgs.Length; i++)
                                    {
                                        positionalArgs[i] = GetSliceForNextToken(ref reader);
                                    }

                                    result.MsgPackPositionalArguments = positionalArgs;
                                    break;
                                case MessagePackType.Map:
                                    int namedArgsCount = reader.ReadMapHeader();
                                    var namedArgs = new Dictionary<string, ReadOnlySequence<byte>>(namedArgsCount);
                                    for (int i = 0; i < namedArgsCount; i++)
                                    {
                                        string propertyName = reader.ReadString();
                                        namedArgs.Add(propertyName, GetSliceForNextToken(ref reader));
                                    }

                                    result.MsgPackNamedArguments = namedArgs;
                                    break;
                                case MessagePackType.Nil:
                                    result.MsgPackPositionalArguments = Array.Empty<ReadOnlySequence<byte>>();
                                    reader.ReadNil();
                                    break;
                                case MessagePackType type:
                                    throw new MessagePackSerializationException("Expected a map or array of arguments but got " + type);
                            }

                            result.MsgPackArguments = reader.Sequence.Slice(paramsTokenStartPosition, reader.Position);

                            break;
                    }
                }

                // If method is $/progress, get the progress instance from the dictionary and call Report
                if (string.Equals(result.Method, MessageFormatterProgressTracker.ProgressRequestSpecialMethod, StringComparison.Ordinal))
                {
                    try
                    {
                        if (result.TryGetArgumentByNameOrIndex("token", 0, typeof(long), out object? tokenObject) && tokenObject is long progressId)
                        {
                            MessageFormatterProgressTracker.ProgressParamInformation? progressInfo = null;
                            if (this.formatter.formatterProgressTracker.TryGetProgressObject(progressId, out progressInfo))
                            {
                                if (result.TryGetArgumentByNameOrIndex("value", 1, progressInfo.ValueType, out object? value))
                                {
                                    progressInfo.InvokeReport(value);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        this.formatter.rpc?.TraceSource.TraceData(TraceEventType.Error, (int)JsonRpc.TraceEvents.ProgressNotificationError, ex);
                    }
                }

                return result;
            }

            public void Serialize(ref MessagePackWriter writer, Protocol.JsonRpcRequest value, MessagePackSerializerOptions options)
            {
                try
                {
                    this.formatter.formatterProgressTracker.RequestIdBeingSerialized = value.RequestId;
                    this.formatter.duplexPipeTracker.RequestIdBeingSerialized = value.RequestId;

                    writer.WriteMapHeader(4);

                    writer.Write(VersionPropertyName);
                    writer.Write(value.Version);

                    writer.Write(IdPropertyName);
                    options.Resolver.GetFormatterWithVerify<RequestId>().Serialize(ref writer, value.RequestId, options);

                    writer.Write(MethodPropertyName);
                    writer.Write(value.Method);

                    writer.Write(ParamsPropertyName);
                    if (value.ArgumentsList != null)
                    {
                        writer.WriteArrayHeader(value.ArgumentsList.Count);
                        foreach (var arg in value.ArgumentsList)
                        {
                            this.formatter.dynamicObjectTypeFormatterForUserSuppliedResolver.Serialize(ref writer, arg, this.formatter.userDataSerializationOptions);
                        }
                    }
                    else if (value.NamedArguments != null)
                    {
                        writer.WriteMapHeader(value.NamedArguments.Count);
                        foreach (KeyValuePair<string, object?> entry in value.NamedArguments)
                        {
                            writer.Write(entry.Key);
                            this.formatter.dynamicObjectTypeFormatterForUserSuppliedResolver.Serialize(ref writer, entry.Value, this.formatter.userDataSerializationOptions);
                        }
                    }
                    else
                    {
                        writer.WriteNil();
                    }
                }
                finally
                {
                    this.formatter.formatterProgressTracker.RequestIdBeingSerialized = default;
                    this.formatter.duplexPipeTracker.RequestIdBeingSerialized = default;
                }
            }
        }

        private class JsonRpcResultFormatter : IMessagePackFormatter<Protocol.JsonRpcResult>
        {
            private readonly MessagePackFormatter formatter;

            internal JsonRpcResultFormatter(MessagePackFormatter formatter)
            {
                this.formatter = formatter;
            }

            public Protocol.JsonRpcResult Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                var result = new JsonRpcResult(this.formatter.userDataSerializationOptions)
                {
                    OriginalMessagePack = reader.Sequence,
                };

                int propertyCount = reader.ReadMapHeader();
                for (int propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
                {
                    switch (reader.ReadString())
                    {
                        case VersionPropertyName:
                            result.Version = reader.ReadString();
                            break;
                        case IdPropertyName:
                            result.RequestId = options.Resolver.GetFormatterWithVerify<RequestId>().Deserialize(ref reader, options);
                            break;
                        case ResultPropertyName:
                            result.MsgPackResult = GetSliceForNextToken(ref reader);
                            break;
                    }
                }

                this.formatter.formatterProgressTracker.OnResponseReceived(result.RequestId);
                this.formatter.duplexPipeTracker.OnResponseReceived(result.RequestId, successful: true);

                return result;
            }

            public void Serialize(ref MessagePackWriter writer, Protocol.JsonRpcResult value, MessagePackSerializerOptions options)
            {
                writer.WriteMapHeader(3);

                writer.Write(VersionPropertyName);
                writer.Write(value.Version);

                writer.Write(IdPropertyName);
                options.Resolver.GetFormatterWithVerify<RequestId>().Serialize(ref writer, value.RequestId, options);

                writer.Write(ResultPropertyName);
                this.formatter.dynamicObjectTypeFormatterForUserSuppliedResolver.Serialize(ref writer, value.Result, this.formatter.userDataSerializationOptions);
            }
        }

        private class JsonRpcErrorFormatter : IMessagePackFormatter<Protocol.JsonRpcError>
        {
            private readonly MessagePackFormatter formatter;

            internal JsonRpcErrorFormatter(MessagePackFormatter formatter)
            {
                this.formatter = formatter;
            }

            public Protocol.JsonRpcError Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                var error = new JsonRpcError
                {
                    OriginalMessagePack = reader.Sequence,
                };

                int propertyCount = reader.ReadMapHeader();
                for (int propertyIdx = 0; propertyIdx < propertyCount; propertyIdx++)
                {
                    switch (reader.ReadString())
                    {
                        case VersionPropertyName:
                            error.Version = reader.ReadString();
                            break;
                        case IdPropertyName:
                            error.RequestId = options.Resolver.GetFormatterWithVerify<RequestId>().Deserialize(ref reader, options);
                            break;
                        case ErrorPropertyName:
                            error.Error = options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcError.ErrorDetail?>().Deserialize(ref reader, options);
                            break;
                    }
                }

                this.formatter.formatterProgressTracker.OnResponseReceived(error.RequestId);
                this.formatter.duplexPipeTracker.OnResponseReceived(error.RequestId, successful: false);

                return error;
            }

            public void Serialize(ref MessagePackWriter writer, Protocol.JsonRpcError value, MessagePackSerializerOptions options)
            {
                writer.WriteMapHeader(3);

                writer.Write(VersionPropertyName);
                writer.Write(value.Version);

                writer.Write(IdPropertyName);
                options.Resolver.GetFormatterWithVerify<RequestId>().Serialize(ref writer, value.RequestId, options);

                writer.Write(ErrorPropertyName);
                options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcError.ErrorDetail?>().Serialize(ref writer, value.Error, options);
            }
        }

        private class JsonRpcErrorDetailFormatter : IMessagePackFormatter<Protocol.JsonRpcError.ErrorDetail>
        {
            private const string CodePropertyName = "code";
            private const string MessagePropertyName = "message";
            private const string DataPropertyName = "data";
            private readonly MessagePackFormatter formatter;

            internal JsonRpcErrorDetailFormatter(MessagePackFormatter formatter)
            {
                this.formatter = formatter;
            }

            public Protocol.JsonRpcError.ErrorDetail Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                var result = new JsonRpcError.ErrorDetail(this.formatter.userDataSerializationOptions);

                int propertyCount = reader.ReadMapHeader();
                for (int propertyIdx = 0; propertyIdx < propertyCount; propertyIdx++)
                {
                    switch (reader.ReadString())
                    {
                        case CodePropertyName:
                            result.Code = options.Resolver.GetFormatterWithVerify<JsonRpcErrorCode>().Deserialize(ref reader, options);
                            break;
                        case MessagePropertyName:
                            result.Message = reader.ReadString();
                            break;
                        case DataPropertyName:
                            result.MsgPackData = GetSliceForNextToken(ref reader);
                            break;
                    }
                }

                return result;
            }

            public void Serialize(ref MessagePackWriter writer, Protocol.JsonRpcError.ErrorDetail value, MessagePackSerializerOptions options)
            {
                writer.WriteMapHeader(3);

                writer.Write(CodePropertyName);
                options.Resolver.GetFormatterWithVerify<JsonRpcErrorCode>().Serialize(ref writer, value.Code, options);

                writer.Write(MessagePropertyName);
                writer.Write(value.Message);

                writer.Write(DataPropertyName);
                this.formatter.dynamicObjectTypeFormatterForUserSuppliedResolver.Serialize(ref writer, value.Data, this.formatter.userDataSerializationOptions);
            }
        }

        [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
        [DataContract]
        private class JsonRpcRequest : Protocol.JsonRpcRequest, IJsonRpcMessageBufferManager, IJsonRpcMessagePackRetention
        {
            private readonly MessagePackFormatter formatter;

            internal JsonRpcRequest(MessagePackFormatter formatter)
            {
                this.formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
            }

            public override int ArgumentCount => this.MsgPackNamedArguments?.Count ?? this.MsgPackPositionalArguments?.Count ?? base.ArgumentCount;

            public ReadOnlySequence<byte> OriginalMessagePack { get; internal set; }

            internal ReadOnlySequence<byte> MsgPackArguments { get; set; }

            internal IReadOnlyDictionary<string, ReadOnlySequence<byte>>? MsgPackNamedArguments { get; set; }

            internal IReadOnlyList<ReadOnlySequence<byte>>? MsgPackPositionalArguments { get; set; }

            void IJsonRpcMessageBufferManager.DeserializationComplete(JsonRpcMessage message)
            {
                Assumes.True(message == this);

                // Clear references to buffers that we are no longer entitled to.
                this.MsgPackNamedArguments = null;
                this.MsgPackPositionalArguments = null;
                this.MsgPackArguments = default;
                this.OriginalMessagePack = default;
            }

            public override ArgumentMatchResult TryGetTypedArguments(ReadOnlySpan<ParameterInfo> parameters, Span<object?> typedArguments)
            {
                if (parameters.Length == 1 && this.MsgPackNamedArguments != null)
                {
                    Assumes.NotNull(this.Method);

                    JsonRpcMethodAttribute? attribute = this.formatter.rpc?.GetJsonRpcMethodAttribute(this.Method, parameters);
                    if (attribute?.UseSingleObjectParameterDeserialization ?? false)
                    {
                        var reader = new MessagePackReader(this.MsgPackArguments);
                        try
                        {
                            typedArguments[0] = MessagePackSerializer.Deserialize(parameters[0].ParameterType, ref reader, this.formatter.userDataSerializationOptions);
                            return ArgumentMatchResult.Success;
                        }
                        catch (NotSupportedException)
                        {
                            // This block can be removed after https://github.com/neuecc/MessagePack-CSharp/pull/633 is applied.
                            return ArgumentMatchResult.ParameterArgumentTypeMismatch;
                        }
                        catch (MessagePackSerializationException)
                        {
                            return ArgumentMatchResult.ParameterArgumentTypeMismatch;
                        }
                    }
                }

                return base.TryGetTypedArguments(parameters, typedArguments);
            }

            public override bool TryGetArgumentByNameOrIndex(string? name, int position, Type? typeHint, out object? value)
            {
                // If anyone asks us for an argument *after* we've been told deserialization is done, there's something very wrong.
                Assumes.True(this.MsgPackNamedArguments != null || this.MsgPackPositionalArguments != null);

                ReadOnlySequence<byte> msgpackArgument = default;
                if (position >= 0 && this.MsgPackPositionalArguments?.Count > position)
                {
                    msgpackArgument = this.MsgPackPositionalArguments[position];
                }
                else if (name is object && this.MsgPackNamedArguments != null)
                {
                    this.MsgPackNamedArguments.TryGetValue(name, out msgpackArgument);
                }

                if (msgpackArgument.IsEmpty)
                {
                    value = null;
                    return false;
                }

                var reader = new MessagePackReader(msgpackArgument);
                try
                {
                    // Deserialization of messages should never occur concurrently for a single instance of a formatter.
                    Assumes.True(this.formatter.duplexPipeTracker.RequestIdBeingDeserialized.IsEmpty);
                    this.formatter.duplexPipeTracker.RequestIdBeingDeserialized = this.RequestId;

                    value = MessagePackSerializer.Deserialize(typeHint ?? typeof(object), ref reader, this.formatter.userDataSerializationOptions);
                    return true;
                }
                catch (NotSupportedException)
                {
                    // This block can be removed after https://github.com/neuecc/MessagePack-CSharp/pull/633 is applied.
                    value = null;
                    return false;
                }
                catch (MessagePackSerializationException)
                {
                    value = null;
                    return false;
                }
                finally
                {
                    this.formatter.duplexPipeTracker.RequestIdBeingDeserialized = default;
                }
            }
        }

        [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
        [DataContract]
        private class JsonRpcResult : Protocol.JsonRpcResult, IJsonRpcMessageBufferManager, IJsonRpcMessagePackRetention
        {
            private readonly MessagePackSerializerOptions serializerOptions;

            internal JsonRpcResult(MessagePackSerializerOptions serializerOptions)
            {
                this.serializerOptions = serializerOptions ?? throw new ArgumentNullException(nameof(serializerOptions));
            }

            public ReadOnlySequence<byte> OriginalMessagePack { get; internal set; }

            internal ReadOnlySequence<byte> MsgPackResult { get; set; }

            void IJsonRpcMessageBufferManager.DeserializationComplete(JsonRpcMessage message)
            {
                Assumes.True(message == this);
                this.MsgPackResult = default;
                this.OriginalMessagePack = default;
            }

            public override T GetResult<T>()
            {
                return this.MsgPackResult.IsEmpty
                    ? (T)this.Result!
                    : MessagePackSerializer.Deserialize<T>(this.MsgPackResult, this.serializerOptions);
            }

            protected internal override void SetExpectedResultType(Type resultType)
            {
                Verify.Operation(!this.MsgPackResult.IsEmpty, "Result is no longer available or has already been deserialized.");

                var reader = new MessagePackReader(this.MsgPackResult);
                this.Result = MessagePackSerializer.Deserialize(resultType, ref reader, this.serializerOptions);
                this.MsgPackResult = default;
            }
        }

        [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
        [DataContract]
        private class JsonRpcError : Protocol.JsonRpcError, IJsonRpcMessageBufferManager, IJsonRpcMessagePackRetention
        {
            public ReadOnlySequence<byte> OriginalMessagePack { get; internal set; }

            void IJsonRpcMessageBufferManager.DeserializationComplete(JsonRpcMessage message)
            {
                Assumes.True(message == this);
                if (this.Error is ErrorDetail privateDetail)
                {
                    privateDetail.MsgPackData = default;
                }

                this.OriginalMessagePack = default;
            }

            [DataContract]
            internal new class ErrorDetail : Protocol.JsonRpcError.ErrorDetail
            {
                private readonly MessagePackSerializerOptions serializerOptions;

                internal ErrorDetail(MessagePackSerializerOptions serializerOptions)
                {
                    this.serializerOptions = serializerOptions ?? throw new ArgumentNullException(nameof(serializerOptions));
                }

                internal ReadOnlySequence<byte> MsgPackData { get; set; }

                public override object? GetData(Type dataType)
                {
                    Requires.NotNull(dataType, nameof(dataType));
                    if (this.MsgPackData.IsEmpty)
                    {
                        return this.Data;
                    }

                    var reader = new MessagePackReader(this.MsgPackData);
                    try
                    {
                        return MessagePackSerializer.Deserialize(dataType, ref reader, this.serializerOptions);
                    }
                    catch (MessagePackSerializationException)
                    {
                        // Deserialization failed. Try returning array/dictionary based primitive objects.
                        try
                        {
                            return MessagePackSerializer.Deserialize<object>(this.MsgPackData, this.serializerOptions.WithResolver(PrimitiveObjectResolver.Instance));
                        }
                        catch (MessagePackSerializationException)
                        {
                            return null;
                        }
                    }
                }

                protected internal override void SetExpectedDataType(Type dataType)
                {
                    Verify.Operation(!this.MsgPackData.IsEmpty, "Data is no longer available or has already been deserialized.");

                    this.Data = this.GetData(dataType);
                }
            }
        }
    }
}