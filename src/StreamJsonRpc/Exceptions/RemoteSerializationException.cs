﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using StreamJsonRpc.Protocol;

namespace StreamJsonRpc;

/// <summary>
/// An exception thrown from back to the client from various <see cref="JsonRpc"/> request methods when the server failed to serialize the response.
/// </summary>
/// <remarks>
/// This exception comes from the <see cref="JsonRpcErrorCode.ResponseSerializationFailure"/> error code.
/// </remarks>
[Serializable]
public class RemoteSerializationException : RemoteRpcException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteSerializationException"/> class.
    /// </summary>
    /// <inheritdoc cref="RemoteRpcException(string?)"/>
    public RemoteSerializationException(string? message, object? errorData, object? deserializedErrorData)
        : base(message)
    {
        this.ErrorCode = JsonRpcErrorCode.ResponseSerializationFailure;
        this.ErrorData = errorData;
        this.DeserializedErrorData = deserializedErrorData;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteSerializationException"/> class.
    /// </summary>
    /// <param name="serializationInfo">Serialization info.</param>
    /// <param name="streamingContext">Streaming context.</param>
#if NET8_0_OR_GREATER
    [Obsolete]
#endif
    protected RemoteSerializationException(System.Runtime.Serialization.SerializationInfo serializationInfo, System.Runtime.Serialization.StreamingContext streamingContext)
        : base(serializationInfo, streamingContext)
    {
        throw new NotImplementedException();
    }
}
