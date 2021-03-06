// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace System.Runtime.Serialization
{
    [Serializable]
    public class SerializationException : SystemException
    {
        private static string s_nullMessage = SR.SerializationException;

        public SerializationException() : base(s_nullMessage)
        {
        }

        public SerializationException(string message) : base(message)
        {
        }

        public SerializationException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected SerializationException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
