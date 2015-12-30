﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;

namespace Internal.TypeSystem
{
    public enum GenericParameterKind
    {
        Type,
        Method,
    }

    /// <summary>
    /// Describes the variance on a generic type parameter of a generic type or method.
    /// </summary>
    public enum GenericVariance
    {
        None = 0,
        
        /// <summary>
        /// The generic type parameter is covariant. A covariant type parameter can appear
        /// as the result type of a method, the type of a read-only field, a declared base
        /// type, or an implemented interface.
        /// </summary>
        Covariant = 1,

        /// <summary>
        /// The generic type parameter is contravariant. A contravariant type parameter can
        /// appear as a parameter type in method signatures.
        /// </summary>
        Contravariant = 2
    }

    /// <summary>
    /// Describes the constraints on a generic type parameter of a generic type or method.
    /// </summary>
    [Flags]
    public enum GenericConstraints
    {
        None = 0,

        /// <summary>
        /// A type can be substituted for the generic type parameter only if it is a reference type.
        /// </summary>
        ReferenceTypeConstraint = 0x04,
        
        /// <summary>
        // A type can be substituted for the generic type parameter only if it is a value
        // type and is not nullable.
        /// </summary>
        NotNullableValueTypeConstraint = 0x08,

        /// <summary>
        /// A type can be substituted for the generic type parameter only if it has a parameterless
        /// constructor.
        /// </summary>
        DefaultConstructorConstraint = 0x10,
    }

    public abstract partial class GenericParameterDesc : TypeDesc
    {
        /// <summary>
        /// Gets a value indicating whether this is a type or method generic parameter.
        /// </summary>
        public abstract GenericParameterKind Kind { get; }
        
        /// <summary>
        /// Gets the zero based index of the generic parameter within the declaring type or method.
        /// </summary>
        public abstract int Index { get; }

        /// <summary>
        /// Gets a value indicating the variance of this generic parameter.
        /// </summary>
        public abstract GenericVariance Variance { get; }

        /// <summary>
        /// Gets a value indicating generic constraints of this generic parameter.
        /// </summary>
        public abstract GenericConstraints Constraints { get; }
        
        /// <summary>
        /// Gets type constraints imposed on substitutions.
        /// </summary>
        public abstract IEnumerable<TypeDesc> TypeConstraints { get; }
    }
}