﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ICSharpCode.Decompiler;
using JSIL.Ast;
using JSIL.Internal;
using Mono.Cecil;

namespace JSIL {
    public static class TypeUtil {
        public static string GetLocalName (TypeDefinition type) {
            var result = new List<string>();
            result.Add(type.Name);

            type = type.DeclaringType;
            while (type != null) {
                result.Insert(0, type.Name);
                type = type.DeclaringType;
            }

            return String.Join("_", result);
        }

        public static bool IsStruct (TypeReference type) {
            if (type == null)
                return false;

            type = DereferenceType(type);
            MetadataType etype = type.MetadataType;

            if (IsEnum(type))
                return false;

            var git = type as GenericInstanceType;
            if (git != null)
                return git.IsValueType;

            return (etype == MetadataType.ValueType);
        }

        public static bool IsNumeric (TypeReference type) {
            type = DereferenceType(type);

            switch (type.MetadataType) {
                case MetadataType.Byte:
                case MetadataType.SByte:
                case MetadataType.Double:
                case MetadataType.Single:
                case MetadataType.Int16:
                case MetadataType.Int32:
                case MetadataType.Int64:
                case MetadataType.UInt16:
                case MetadataType.UInt32:
                case MetadataType.UInt64:
                    return true;
                    // Blech
                case MetadataType.UIntPtr:
                case MetadataType.IntPtr:
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsIntegral (TypeReference type) {
            type = DereferenceType(type);

            switch (type.MetadataType) {
                case MetadataType.Byte:
                case MetadataType.SByte:
                case MetadataType.Int16:
                case MetadataType.Int32:
                case MetadataType.Int64:
                case MetadataType.UInt16:
                case MetadataType.UInt32:
                case MetadataType.UInt64:
                    return true;
                    // Blech
                case MetadataType.UIntPtr:
                case MetadataType.IntPtr:
                    return true;
                default:
                    return false;
            }
        }

        public static TypeReference StripNullable (TypeReference type) {
            var git = type as GenericInstanceType;
            if ((git != null) && (git.Name == "Nullable`1")) {
                return git.GenericArguments[0];
            }

            return type;
        }

        public static bool IsEnum (TypeReference type) {
            var typedef = GetTypeDefinition(type);
            return (typedef != null) && (typedef.IsEnum);
        }

        public static bool IsBoolean (TypeReference type) {
            type = DereferenceType(type);
            return type.MetadataType == MetadataType.Boolean;
        }

        public static bool IsIgnoredType (TypeReference type) {
            type = DereferenceType(type);

            if (type == null)
                return false;
            else if (type.IsPointer)
                return true;
            else if (type.IsPinned)
                return true;
            else if (type.IsFunctionPointer)
                return true;
            else
                return false;
        }

        public static bool IsDelegateType (TypeReference type) {
            type = DereferenceType(type);

            var typedef = GetTypeDefinition(type);
            if (typedef == null)
                return false;
            if (typedef.FullName == "System.Delegate")
                return true;

            if (
                (typedef != null) && (typedef.BaseType != null) &&
                (
                    (typedef.BaseType.FullName == "System.Delegate") ||
                    (typedef.BaseType.FullName == "System.MulticastDelegate")
                )
                ) {
                    return true;
                }

            return false;
        }

        public static bool IsOpenType (TypeReference type) {
            type = DereferenceType(type);

            var gp = type as GenericParameter;
            var git = type as GenericInstanceType;
            var at = type as ArrayType;
            var byref = type as ByReferenceType;

            if (gp != null)
                return true;

            if (git != null) {
                var elt = git.ElementType;

                foreach (var ga in git.GenericArguments) {
                    if (IsOpenType(ga))
                        return true;
                }

                return IsOpenType(elt);
            }

            if (at != null)
                return IsOpenType(at.ElementType);

            if (byref != null)
                return IsOpenType(byref.ElementType);

            return false;
        }

        public static TypeDefinition GetTypeDefinition (TypeReference typeRef, bool mapAllArraysToSystemArray = true) {
            if (typeRef == null)
                return null;

            var ts = typeRef.Module.TypeSystem;
            typeRef = DereferenceType(typeRef);

            bool unwrapped = false;
            do {
                var rmt = typeRef as RequiredModifierType;
                var omt = typeRef as OptionalModifierType;

                if (rmt != null) {
                    typeRef = rmt.ElementType;
                    unwrapped = true;
                } else if (omt != null) {
                    typeRef = omt.ElementType;
                    unwrapped = true;
                } else {
                    unwrapped = false;
                }
            } while (unwrapped);

            var at = typeRef as ArrayType;
            if (at != null) {
                if (mapAllArraysToSystemArray)
                    return new TypeReference(ts.Object.Namespace, "Array", ts.Object.Module, ts.Object.Scope).ResolveOrThrow();

                var inner = GetTypeDefinition(at.ElementType, mapAllArraysToSystemArray);
                if (inner != null)
                    return (new ArrayType(inner, at.Rank)).Resolve();
                else
                    return null;
            }

            var gp = typeRef as GenericParameter;
            if ((gp != null) && (gp.Owner == null))
                return null;

            else if (IsIgnoredType(typeRef))
                return null;
            else
                return typeRef.Resolve();
        }

        public static TypeReference FullyDereferenceType (TypeReference type, out int depth) {
            depth = 0;

            var brt = type as ByReferenceType;
            while (brt != null) {
                depth += 1;
                type = brt.ElementType;
                brt = type as ByReferenceType;
            }

            return type;
        }

        public static TypeReference DereferenceType (TypeReference type, bool dereferencePointers = false) {
            var brt = type as ByReferenceType;
            if (brt != null)
                return brt.ElementType;

            if (dereferencePointers) {
                var pt = type as PointerType;
                if (pt != null)
                    return pt.ElementType;
            }

            return type;
        }

        private static bool TypeInBases (TypeReference haystack, TypeReference needle, bool explicitGenericEquality) {
            if ((haystack == null) || (needle == null))
                return haystack == needle;

            var dToWalk = haystack.Resolve();
            var dSource = needle.Resolve();

            if ((dToWalk == null) || (dSource == null))
                return TypesAreEqual(haystack, needle, explicitGenericEquality);

            var t = haystack;
            while (t != null) {
                if (TypesAreEqual(t, needle, explicitGenericEquality))
                    return true;

                var dT = t.Resolve();

                if ((dT != null) && (dT.BaseType != null)) {
                    var baseType = dT.BaseType;

                    t = baseType;
                } else
                    break;
            }

            return false;
        }

        public static bool TypesAreEqual (TypeReference target, TypeReference source, bool strictEquality = false) {
            if (target == source)
                return true;
            else if ((target == null) || (source == null))
                return (target == source);

            bool result;

            int targetDepth, sourceDepth;
            FullyDereferenceType(target, out targetDepth);
            FullyDereferenceType(source, out sourceDepth);

            var targetGp = target as GenericParameter;
            var sourceGp = source as GenericParameter;

            if ((targetGp != null) || (sourceGp != null)) {
                if ((targetGp == null) || (sourceGp == null))
                    return false;

                // https://github.com/jbevain/cecil/issues/97

                var targetOwnerType = targetGp.Owner as TypeReference;
                var sourceOwnerType = sourceGp.Owner as TypeReference;

                if ((targetOwnerType != null) || (sourceOwnerType != null)) {
                    var basesEqual = false;

                    if (TypeInBases(targetOwnerType, sourceOwnerType, strictEquality))
                        basesEqual = true;
                    else if (TypeInBases(sourceOwnerType, targetOwnerType, strictEquality))
                        basesEqual = true;

                    if (!basesEqual)
                        return false;
                } else {
                    if (targetGp.Owner != sourceGp.Owner)
                        return false;
                }

                if (targetGp.Type != sourceGp.Type)
                    return false;

                return (targetGp.Name == sourceGp.Name) && (targetGp.Position == sourceGp.Position);
            }

            var targetArray = target as ArrayType;
            var sourceArray = source as ArrayType;

            if ((targetArray != null) || (sourceArray != null)) {
                if ((targetArray == null) || (sourceArray == null))
                    return false;

                if (targetArray.Rank != sourceArray.Rank)
                    return false;

                return TypesAreEqual(targetArray.ElementType, sourceArray.ElementType, strictEquality);
            }

            var targetGit = target as GenericInstanceType;
            var sourceGit = source as GenericInstanceType;

            if ((targetGit != null) || (sourceGit != null)) {
                if (!strictEquality) {
                    if ((targetGit != null) && TypesAreEqual(targetGit.ElementType, source))
                        return true;
                    if ((sourceGit != null) && TypesAreEqual(target, sourceGit.ElementType))
                        return true;
                }

                if ((targetGit == null) || (sourceGit == null))
                    return false;

                if (targetGit.GenericArguments.Count != sourceGit.GenericArguments.Count)
                    return false;

                for (var i = 0; i < targetGit.GenericArguments.Count; i++) {
                    if (!TypesAreEqual(targetGit.GenericArguments[i], sourceGit.GenericArguments[i], strictEquality))
                        return false;
                }

                return TypesAreEqual(targetGit.ElementType, sourceGit.ElementType, strictEquality);
            }

            if ((target.IsByReference != source.IsByReference) || (targetDepth != sourceDepth))
                result = false;
            else if (target.IsPointer != source.IsPointer)
                result = false;
            else if (target.IsFunctionPointer != source.IsFunctionPointer)
                result = false;
            else if (target.IsPinned != source.IsPinned)
                result = false;
            else {
                if (
                    (target.Name == source.Name) &&
                    (target.Namespace == source.Namespace) &&
                    (target.Module == source.Module) &&
                    TypesAreEqual(target.DeclaringType, source.DeclaringType, strictEquality)
                )
                    return true;

                var dTarget = GetTypeDefinition(target);
                var dSource = GetTypeDefinition(source);

                if (Equals(dTarget, dSource) && (dSource != null))
                    result = true;
                else if (Equals(target, source))
                    result = true;
                else if (
                    (dTarget != null) && (dSource != null) &&
                    (dTarget.FullName == dSource.FullName)
                )
                    result = true;
                else
                    result = false;
            }

            return result;
        }

        public static IEnumerable<TypeDefinition> AllBaseTypesOf (TypeDefinition type) {
            if (type == null)
                yield break;

            var baseType = GetTypeDefinition(type.BaseType);

            while (baseType != null) {
                yield return baseType;

                baseType = GetTypeDefinition(baseType.BaseType);
            }
        }

        public static bool TypesAreAssignable (ITypeInfoSource typeInfo, TypeReference target, TypeReference source) {
            if ((target == null) || (source == null))
                return false;

            // All values are assignable to object
            if (target.FullName == "System.Object")
                return true;

            int targetDepth, sourceDepth;
            if (TypesAreEqual(FullyDereferenceType(target, out targetDepth), FullyDereferenceType(source, out sourceDepth))) {
                if (targetDepth == sourceDepth)
                    return true;
            }

            // Complex hack necessary because System.Array and T[] do not implement IEnumerable<T>
            var targetGit = target as GenericInstanceType;
            if (
                (targetGit != null) &&
                (targetGit.Name == "IEnumerable`1") &&
                source.IsArray &&
                (targetGit.GenericArguments.FirstOrDefault() == source.GetElementType())
            )
                return true;

            var cacheKey = new Tuple<string, string>(target.FullName, source.FullName);
            return typeInfo.AssignabilityCache.GetOrCreate(
                cacheKey, () => {
                    bool result = false;

                    var dSource = GetTypeDefinition(source);

                    if (TypeInBases(source, target, false))
                        result = true;
                    else if (dSource == null)
                        result = false;
                    else if (TypesAreEqual(target, dSource))
                        result = true;
                    else if ((dSource.BaseType != null) && TypesAreAssignable(typeInfo, target, dSource.BaseType))
                        result = true;
                    else {
                        foreach (var iface in dSource.Interfaces) {
                            if (TypesAreAssignable(typeInfo, target, iface)) {
                                result = true;
                                break;
                            }
                        }
                    }

                    return result;
                }
            );
        }

        public static bool IsIntegralOrEnum (TypeReference type) {
            var typedef = GetTypeDefinition(type);
            return IsIntegral(type) || ((typedef != null) && typedef.IsEnum);
        }

        public static bool IsNumericOrEnum (TypeReference type) {
            var typedef = GetTypeDefinition(type);
            return IsNumeric(type) || ((typedef != null) && typedef.IsEnum);
        }

        public static JSExpression[] GetArrayDimensions (TypeReference arrayType) {
            var at = arrayType as ArrayType;
            if (at == null)
                return null;

            var result = new List<JSExpression>();
            for (var i = 0; i < at.Dimensions.Count; i++) {
                var dim = at.Dimensions[i];
                if (dim.IsSized)
                    result.Add(JSLiteral.New(dim.UpperBound.Value));
                else
                    return null;
            }

            return result.ToArray();
        }
    }
}
