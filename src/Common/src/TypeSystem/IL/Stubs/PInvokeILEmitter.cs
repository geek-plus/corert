// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

using Internal.TypeSystem;
using Internal.TypeSystem.Interop;
using Debug = System.Diagnostics.Debug;

namespace Internal.IL.Stubs
{
    /// <summary>
    /// Provides method bodies for PInvoke methods
    /// 
    /// This by no means intends to provide full PInvoke support. The intended use of this is to
    /// a) prevent calls getting generated to targets that require a full marshaller
    /// (this compiler doesn't provide that), and b) offer a hand in some very simple marshalling
    /// situations (but support for this part might go away as the product matures).
    /// </summary>
    public struct PInvokeILEmitter
    {
        private readonly PInvokeMethodData _methodData;
        private readonly Marshaller[] _marshallers;
        private PInvokeILEmitter(MethodDesc targetMethod, PInvokeILEmitterConfiguration pinvokeILEmitterConfiguration)
        {
            Debug.Assert(targetMethod.IsPInvoke);

            _methodData = new PInvokeMethodData(targetMethod, pinvokeILEmitterConfiguration);

            _marshallers = InitializeMarshallers(_methodData);
        }

       private static Marshaller[] InitializeMarshallers(PInvokeMethodData pInvokeMethodData)
        {
            MethodDesc targetMethod = pInvokeMethodData.TargetMethod;
            MethodSignature methodSig = targetMethod.Signature;
            ParameterMetadata[] parameterMetadataArray = targetMethod.GetParameterMetadata();
            Marshaller[] marshallers = new Marshaller[methodSig.Length + 1];
            int parameterIndex = 0;
            ParameterMetadata parameterMetadata = new ParameterMetadata();
            for (int i = 0; i < marshallers.Length; i++)
            {
                Debug.Assert(parameterIndex == parameterMetadataArray.Length || i <= parameterMetadataArray[parameterIndex].Index);
                if (parameterIndex == parameterMetadataArray.Length || i < parameterMetadataArray[parameterIndex].Index)
                {
                    // if we don't have metadata for the parameter, create a dummy one
                    parameterMetadata = new ParameterMetadata(i, ParameterMetadataAttributes.None, null);
                }
                else if (i == parameterMetadataArray[parameterIndex].Index)
                {
                    parameterMetadata = parameterMetadataArray[parameterIndex++];
                }
                TypeDesc parameterType = (i == 0) ? methodSig.ReturnType : methodSig[i - 1];  //first item is the return type
                marshallers[i] = Marshaller.CreateMarshaller(parameterType, pInvokeMethodData, parameterMetadata);
            }

            return marshallers;
        }

        private MethodIL EmitIL()
        {
            // We have 4 code streams:
            // - _marshallingCodeStream is used to convert each argument into a native type and 
            // store that into the local
            // - callsiteSetupCodeStream is used to used to load each previously generated local
            // and call the actual target native method.
            // - _returnValueMarshallingCodeStream is used to convert the native return value 
            // to managed one.
            // - _unmarshallingCodestream is used to propagate [out] native arguments values to 
            // managed ones.

            ILEmitter emitter = new ILEmitter();
            ILCodeStream fnptrLoadStream = emitter.NewCodeStream();
            ILCodeStream marshallingCodeStream = emitter.NewCodeStream();
            ILCodeStream callsiteSetupCodeStream = emitter.NewCodeStream();
            ILCodeStream returnValueMarshallingCodeStream = emitter.NewCodeStream();
            ILCodeStream unmarshallingCodestream = emitter.NewCodeStream();

            // Marshal the arguments
            for (int i = 0; i < _marshallers.Length; i++)
            {
                Marshaller marshaller = _marshallers[i];
                marshaller.EmitMarshallingIL(emitter, marshallingCodeStream, callsiteSetupCodeStream, unmarshallingCodestream, returnValueMarshallingCodeStream);
            }

            // make the call
            TypeDesc nativeReturnType = _marshallers[0].NativeType;
            TypeDesc[] nativeParameterTypes = new TypeDesc[_marshallers.Length - 1];

            for (int i = 1; i < _marshallers.Length; i++)
            {
                nativeParameterTypes[i - 1] = _marshallers[i].NativeType;
            }

            MethodDesc targetMethod = _methodData.TargetMethod;
            PInvokeMetadata importMetadata = _methodData.ImportMetadata;
            PInvokeILEmitterConfiguration pinvokeILEmitterConfiguration = _methodData.PInvokeILEmitterConfiguration;

            // if the SetLastError flag is set in DllImport, clear the error code before doing P/Invoke 
            if ((importMetadata.Attributes & PInvokeAttributes.SetLastError) == PInvokeAttributes.SetLastError)
            {
                callsiteSetupCodeStream.Emit(ILOpcode.call, emitter.NewToken(
                            _methodData.PInvokeMarshal.GetKnownMethod("ClearLastWin32Error", null)));
            }

            if (MarshalHelpers.UseLazyResolution(targetMethod, importMetadata.Module, pinvokeILEmitterConfiguration))
            {
                MetadataType lazyHelperType = targetMethod.Context.GetHelperType("InteropHelpers");
                FieldDesc lazyDispatchCell = new PInvokeLazyFixupField((DefType)targetMethod.OwningType, importMetadata);
                fnptrLoadStream.Emit(ILOpcode.ldsflda, emitter.NewToken(lazyDispatchCell));
                fnptrLoadStream.Emit(ILOpcode.call, emitter.NewToken(lazyHelperType.GetKnownMethod("ResolvePInvoke", null)));

                MethodSignatureFlags unmanagedCallConv = PInvokeMetadata.GetUnmanagedCallingConvention(importMetadata.Attributes);

                MethodSignature nativeCalliSig = new MethodSignature(
                    targetMethod.Signature.Flags | unmanagedCallConv, 0, nativeReturnType, nativeParameterTypes);

                ILLocalVariable vNativeFunctionPointer = emitter.NewLocal(targetMethod.Context.GetWellKnownType(WellKnownType.IntPtr));
                fnptrLoadStream.EmitStLoc(vNativeFunctionPointer);
                callsiteSetupCodeStream.EmitLdLoc(vNativeFunctionPointer);
                callsiteSetupCodeStream.Emit(ILOpcode.calli, emitter.NewToken(nativeCalliSig));
            }
            else
            {
                // Eager call
                PInvokeMetadata nativeImportMetadata =
                    new PInvokeMetadata(importMetadata.Module, importMetadata.Name ?? targetMethod.Name, importMetadata.Attributes);

                MethodSignature nativeSig = new MethodSignature(
                    targetMethod.Signature.Flags, 0, nativeReturnType, nativeParameterTypes);

                MethodDesc nativeMethod =
                    new PInvokeTargetNativeMethod(targetMethod.OwningType, nativeSig, nativeImportMetadata, pinvokeILEmitterConfiguration.GetNextNativeMethodId());

                callsiteSetupCodeStream.Emit(ILOpcode.call, emitter.NewToken(nativeMethod));
            }
            
            // if the SetLastError flag is set in DllImport, call the PInvokeMarshal.SaveLastWin32Error so that last error can be used later 
            // by calling PInvokeMarshal.GetLastWin32Error
            if ((importMetadata.Attributes & PInvokeAttributes.SetLastError) == PInvokeAttributes.SetLastError)
            {
                callsiteSetupCodeStream.Emit(ILOpcode.call, emitter.NewToken(
                            _methodData.PInvokeMarshal.GetKnownMethod("SaveLastWin32Error", null)));
            }

            unmarshallingCodestream.Emit(ILOpcode.ret);

            return emitter.Link(targetMethod);
        }

        public static MethodIL EmitIL(MethodDesc method, PInvokeILEmitterConfiguration pinvokeILEmitterConfiguration)
        {
            try
            {
                return new PInvokeILEmitter(method, pinvokeILEmitterConfiguration).EmitIL();
            }
            catch (NotSupportedException)
            {
                ILEmitter emitter = new ILEmitter();
                string message = "Method '" + method.ToString() +
                    "' requires non-trivial marshalling that is not yet supported by this compiler.";

                TypeSystemContext context = method.Context;
                MethodSignature ctorSignature = new MethodSignature(0, 0, context.GetWellKnownType(WellKnownType.Void),
                    new TypeDesc[] { context.GetWellKnownType(WellKnownType.String) });
                MethodDesc exceptionCtor = method.Context.GetWellKnownType(WellKnownType.Exception).GetKnownMethod(".ctor", ctorSignature);

                ILCodeStream codeStream = emitter.NewCodeStream();
                codeStream.Emit(ILOpcode.ldstr, emitter.NewToken(message));
                codeStream.Emit(ILOpcode.newobj, emitter.NewToken(exceptionCtor));
                codeStream.Emit(ILOpcode.throw_);
                codeStream.Emit(ILOpcode.ret);

                return emitter.Link(method);
            }
        }
    }

    /// <summary>
    /// Synthetic method that represents the actual PInvoke target method.
    /// All parameters are simple types. There will be no code
    /// generated for this method. Instead, a static reference to a symbol will be emitted.
    /// </summary>
    internal sealed class PInvokeTargetNativeMethod : MethodDesc
    {
        private TypeDesc _owningType;
        private MethodSignature _signature;
        private PInvokeMetadata _methodMetadata;
        private int _sequenceNumber;

        public PInvokeTargetNativeMethod(TypeDesc owningType, MethodSignature signature, PInvokeMetadata methodMetadata, int sequenceNumber)
        {
            _owningType = owningType;
            _signature = signature;
            _methodMetadata = methodMetadata;
            _sequenceNumber = sequenceNumber;
        }

        public override TypeSystemContext Context
        {
            get
            {
                return _owningType.Context;
            }
        }

        public override TypeDesc OwningType
        {
            get
            {
                return _owningType;
            }
        }

        public override MethodSignature Signature
        {
            get
            {
                return _signature;
            }
        }

        public override string Name
        {
            get
            {
                return "__pInvokeImpl" + _methodMetadata.Name + _sequenceNumber;
            }
        }

        public override bool HasCustomAttribute(string attributeNamespace, string attributeName)
        {
            return false;
        }

        public override bool IsPInvoke
        {
            get
            {
                return true;
            }
        }

        public override PInvokeMetadata GetPInvokeMethodMetadata()
        {
            return _methodMetadata;
        }

        public override string ToString()
        {
            return "[EXTERNAL]" + Name;
        }
    }

    /// <summary>
    /// Synthetic RVA static field that represents PInvoke fixup cell. The RVA data is
    /// backed by a small data structure generated on the fly from the <see cref="PInvokeMetadata"/>
    /// carried by the instance of this class.
    /// </summary>
    internal sealed class PInvokeLazyFixupField : FieldDesc
    {
        private DefType _owningType;
        private PInvokeMetadata _pInvokeMetadata;

        public PInvokeLazyFixupField(DefType owningType, PInvokeMetadata pInvokeMetadata)
        {
            _owningType = owningType;
            _pInvokeMetadata = pInvokeMetadata;
        }

        public PInvokeMetadata PInvokeMetadata
        {
            get
            {
                return _pInvokeMetadata;
            }
        }

        public override TypeSystemContext Context
        {
            get
            {
                return _owningType.Context;
            }
        }

        public override TypeDesc FieldType
        {
            get
            {
                return Context.GetHelperType("InteropHelpers").GetNestedType("MethodFixupCell");
            }
        }

        public override bool HasRva
        {
            get
            {
                return true;
            }
        }

        public override bool IsInitOnly
        {
            get
            {
                return false;
            }
        }

        public override bool IsLiteral
        {
            get
            {
                return false;
            }
        }

        public override bool IsStatic
        {
            get
            {
                return true;
            }
        }

        public override bool IsThreadStatic
        {
            get
            {
                return false;
            }
        }

        public override DefType OwningType
        {
            get
            {
                return _owningType;
            }
        }

        public override bool HasCustomAttribute(string attributeNamespace, string attributeName)
        {
            return false;
        }
    }
}
