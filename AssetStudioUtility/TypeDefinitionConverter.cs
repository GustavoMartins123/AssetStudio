using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.CecilTools;
using Unity.SerializationLogic;

namespace AssetStudio
{
    public class TypeDefinitionConverter
    {
        private sealed class ConversionState
        {
            public readonly Dictionary<string, int> ActiveTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            public bool HasSerializeReference;
        }

        private const int MaxCycleDepth = 10;
        private const int MaxAbsoluteIndentDepth = 60;

        private readonly TypeDefinition TypeDef;
        private readonly TypeResolver TypeResolver;
        private readonly SerializedTypeHelper Helper;
        private readonly int Indent;
        private readonly ConversionState State;
        private readonly bool IsRoot;
        private readonly string? ActiveTypeKey;

        public TypeDefinitionConverter(TypeDefinition typeDef, SerializedTypeHelper helper, int indent)
            : this(typeDef, new TypeResolver(), helper, indent, new ConversionState(), isRoot: true, activeTypeKey: null)
        {
        }

        private TypeDefinitionConverter(
            TypeDefinition typeDef,
            TypeResolver typeResolver,
            SerializedTypeHelper helper,
            int indent,
            ConversionState state,
            bool isRoot,
            string? activeTypeKey)
        {
            TypeDef = typeDef;
            TypeResolver = typeResolver;
            Helper = helper;
            Indent = indent;
            State = state;
            IsRoot = isRoot;
            ActiveTypeKey = activeTypeKey;
        }

        public List<TypeTreeNode> ConvertToTypeTreeNodes()
        {
            var nodes = new List<TypeTreeNode>();
            if (TypeDef == null || Indent > MaxAbsoluteIndentDepth)
            {
                return nodes;
            }

            var typeKey = ActiveTypeKey ?? TypeDef.FullName;
            State.ActiveTypeCounts.TryGetValue(typeKey, out var prevCount);
            if (prevCount >= MaxCycleDepth)
            {
                return nodes;
            }

            State.ActiveTypeCounts[typeKey] = prevCount + 1;
            try
            {
                var baseTypes = new Stack<(TypeReference RawBaseType, GenericInstanceType? AddedGeneric)>();
                var visitedBaseTypes = new HashSet<string>(StringComparer.Ordinal);
                var lastBaseType = TypeDef.BaseType;
                while (lastBaseType != null && !UnitySerializationLogic.IsNonSerialized(lastBaseType))
                {
                    var resolvedBaseRef = TypeResolver.Resolve(lastBaseType);
                    if (!visitedBaseTypes.Add(resolvedBaseRef.FullName))
                    {
                        break;
                    }
                    var resolvedBaseType = resolvedBaseRef.Resolve();
                    if (resolvedBaseType == null)
                    {
                        break;
                    }
                    GenericInstanceType? addedGeneric = null;
                    if (resolvedBaseRef is GenericInstanceType genericInstanceType)
                    {
                        try
                        {
                            TypeResolver.Add(genericInstanceType);
                            addedGeneric = genericInstanceType;
                        }
                        catch (ArgumentException)
                        {
                            // Same generic type definition already bound in resolver with different arguments.
                        }
                    }
                    baseTypes.Push((resolvedBaseRef, addedGeneric));
                    lastBaseType = resolvedBaseType.BaseType;
                }
                while (baseTypes.Count > 0)
                {
                    var (typeReference, addedGeneric) = baseTypes.Pop();
                    var typeDefinition = typeReference.Resolve();
                    if (typeDefinition != null)
                    {
                        foreach (var fieldDefinition in typeDefinition.Fields.Where(WillUnitySerialize))
                        {
                            if (!IsHiddenByParentClass(baseTypes.Select(x => x.RawBaseType), fieldDefinition, TypeDef))
                            {
                                nodes.AddRange(ProcessingFieldDef(fieldDefinition, ResolveGenericFieldReference(fieldDefinition)));
                            }
                        }
                    }

                    if (addedGeneric != null)
                    {
                        TypeResolver.Remove(addedGeneric);
                    }
                }
                foreach (var field in FilteredFields())
                {
                    nodes.AddRange(ProcessingFieldDef(field, field));
                }

                if (IsRoot && State.HasSerializeReference)
                {
                    Helper.AddManagedReferencesRegistry(nodes, "references", Indent);
                }

                return nodes;
            }
            finally
            {
                if (prevCount == 0)
                {
                    State.ActiveTypeCounts.Remove(typeKey);
                }
                else
                {
                    State.ActiveTypeCounts[typeKey] = prevCount;
                }
            }
        }

        private bool WillUnitySerialize(FieldDefinition fieldDefinition)
        {
            try
            {
                var resolvedFieldType = TypeResolver.Resolve(fieldDefinition.FieldType);
                if (UnitySerializationLogic.ShouldNotTryToResolve(resolvedFieldType))
                {
                    return false;
                }
                if (!UnitySerializationLogic.HasSerializeReferenceAttribute(fieldDefinition)
                    && !UnityEngineTypePredicates.IsUnityEngineObject(resolvedFieldType))
                {
                    if (resolvedFieldType.FullName == fieldDefinition.DeclaringType.FullName)
                    {
                        return false;
                    }
                }
                return UnitySerializationLogic.WillUnitySerialize(fieldDefinition, TypeResolver);
            }
            catch (Exception ex)
            {
                throw new Exception(string.Format("Exception while processing {0} {1}, error {2}", fieldDefinition.FieldType.FullName, fieldDefinition.FullName, ex.Message));
            }
        }

        private static bool IsHiddenByParentClass(IEnumerable<TypeReference> parentTypes, FieldDefinition fieldDefinition, TypeDefinition processingType)
        {
            return processingType.Fields.Any(f => f.Name == fieldDefinition.Name) || parentTypes.Any(t => t.Resolve()?.Fields.Any(f => f.Name == fieldDefinition.Name) == true);
        }

        private IEnumerable<FieldDefinition> FilteredFields()
        {
            return TypeDef.Fields.Where(WillUnitySerialize).Where(f =>
            {
                if (UnitySerializationLogic.HasSerializeReferenceAttribute(f))
                {
                    return true;
                }

                var resolvedType = TypeResolver.Resolve(f.FieldType);
                return UnitySerializationLogic.IsSupportedCollection(resolvedType)
                    || !resolvedType.IsGenericInstance
                    || UnityEngineTypePredicates.IsSerializableUnityStruct(resolvedType)
                    || UnityEngineTypePredicates.IsSerializableUnityClass(resolvedType)
                    || UnitySerializationLogic.ShouldImplementIDeserializable(resolvedType);
            });
        }

        private FieldReference ResolveGenericFieldReference(FieldReference fieldRef)
        {
            var field = new FieldReference(fieldRef.Name, fieldRef.FieldType, ResolveDeclaringType(fieldRef.DeclaringType));
            return TypeDef.Module.ImportReference(field);
        }

        private TypeReference ResolveDeclaringType(TypeReference declaringType)
        {
            var typeDefinition = declaringType.Resolve();
            if (typeDefinition == null)
            {
                return declaringType;
            }
            if (!typeDefinition.HasGenericParameters)
            {
                return typeDefinition;
            }
            var genericInstanceType = new GenericInstanceType(typeDefinition);
            foreach (var genericParameter in typeDefinition.GenericParameters)
            {
                genericInstanceType.GenericArguments.Add(genericParameter);
            }
            return TypeResolver.Resolve(genericInstanceType);
        }

        private List<TypeTreeNode> ProcessingFieldDef(FieldDefinition fieldDefinition, FieldReference fieldRef)
        {
            var typeRef = TypeResolver.Resolve(fieldRef.FieldType);
            if (UnitySerializationLogic.HasSerializeReferenceAttribute(fieldDefinition))
            {
                return SerializeReferenceFieldToTypeTreeNodes(typeRef, fieldRef.Name, Indent);
            }

            return TypeRefToTypeTreeNodes(typeRef, fieldRef.Name, Indent, false);
        }

        private List<TypeTreeNode> SerializeReferenceFieldToTypeTreeNodes(TypeReference typeRef, string name, int indent)
        {
            var nodes = new List<TypeTreeNode>();
            if (typeRef == null || indent > MaxAbsoluteIndentDepth)
            {
                return nodes;
            }

            State.HasSerializeReference = true;
            if (CecilUtils.IsGenericList(typeRef) || typeRef.IsArray)
            {
                nodes.Add(new TypeTreeNode(typeRef.Name, name, indent, false));
                Helper.AddArray(nodes, indent + 1);
                Helper.AddManagedReference(nodes, "data", indent + 2);
            }
            else
            {
                Helper.AddManagedReference(nodes, name, indent);
            }

            return nodes;
        }

        private static bool IsStruct(TypeReference typeRef)
        {
            return typeRef.IsValueType && !IsEnum(typeRef) && !typeRef.IsPrimitive;
        }

        private static bool IsEnum(TypeReference typeRef)
        {
            return !typeRef.IsArray && typeRef.Resolve()?.IsEnum == true;
        }

        private static TypeReference? GetEnumUnderlyingType(TypeReference typeRef)
        {
            var typeDef = typeRef.Resolve();
            if (typeDef == null || !typeDef.IsEnum)
            {
                return null;
            }

            foreach (var field in typeDef.Fields)
            {
                if (!field.IsStatic && field.Name == "value__")
                {
                    return field.FieldType;
                }
            }

            foreach (var field in typeDef.Fields)
            {
                if (!field.IsStatic)
                {
                    return field.FieldType;
                }
            }

            return null;
        }

        private static string GetPrimitiveTypeTreeName(MetadataType metadataType)
        {
            return metadataType switch
            {
                MetadataType.Boolean => "bool",
                MetadataType.Byte => "UInt8",
                MetadataType.SByte => "SInt8",
                MetadataType.Int16 => "SInt16",
                MetadataType.UInt16 => "UInt16",
                MetadataType.Int32 => "SInt32",
                MetadataType.UInt32 => "UInt32",
                MetadataType.Int64 => "SInt64",
                MetadataType.UInt64 => "UInt64",
                MetadataType.Char => "char",
                MetadataType.Double => "double",
                MetadataType.Single => "float",
                _ => "SInt32"
            };
        }

        private static bool RequiresAlignment(TypeReference typeRef)
        {
            if (IsEnum(typeRef))
            {
                var underlying = GetEnumUnderlyingType(typeRef);
                return underlying != null && RequiresAlignment(underlying);
            }

            switch (typeRef.MetadataType)
            {
                case MetadataType.Boolean:
                case MetadataType.Char:
                case MetadataType.SByte:
                case MetadataType.Byte:
                case MetadataType.Int16:
                case MetadataType.UInt16:
                    return true;
                default:
                    return UnitySerializationLogic.IsSupportedCollection(typeRef) && RequiresAlignment(CecilUtils.ElementTypeOfCollection(typeRef));
            }
        }

        private static bool IsSystemString(TypeReference typeRef)
        {
            return typeRef.FullName == "System.String";
        }

        private List<TypeTreeNode> TypeRefToTypeTreeNodes(TypeReference typeRef, string name, int indent, bool isElement)
        {
            var nodes = new List<TypeTreeNode>();
            if (typeRef == null || indent > MaxAbsoluteIndentDepth)
            {
                return nodes;
            }

            var align = false;

            if (TypeDef == null || !IsStruct(TypeDef) || !UnityEngineTypePredicates.IsUnityEngineValueType(TypeDef))
            {
                if (IsStruct(typeRef) || RequiresAlignment(typeRef))
                {
                    align = true;
                }
            }
            if (typeRef.IsPrimitive)
            {
                var primitiveName = typeRef.Name;
                switch (primitiveName)
                {
                    case "Boolean":
                        primitiveName = "bool";
                        break;
                    case "Byte":
                        primitiveName = "UInt8";
                        break;
                    case "SByte":
                        primitiveName = "SInt8";
                        break;
                    case "Int16":
                        primitiveName = "SInt16";
                        break;
                    case "UInt16":
                        primitiveName = "UInt16";
                        break;
                    case "Int32":
                        primitiveName = "SInt32";
                        break;
                    case "UInt32":
                        primitiveName = "UInt32";
                        break;
                    case "Int64":
                        primitiveName = "SInt64";
                        break;
                    case "UInt64":
                        primitiveName = "UInt64";
                        break;
                    case "Char":
                        primitiveName = "char";
                        break;
                    case "Double":
                        primitiveName = "double";
                        break;
                    case "Single":
                        primitiveName = "float";
                        break;
                    default:
                        throw new NotSupportedException();
                }
                if (isElement)
                {
                    align = false;
                }
                nodes.Add(new TypeTreeNode(primitiveName, name, indent, align));
            }
            else if (IsSystemString(typeRef))
            {
                Helper.AddString(nodes, name, indent);
            }
            else if (IsEnum(typeRef))
            {
                var underlying = GetEnumUnderlyingType(typeRef);
                var enumTypeName = underlying != null ? GetPrimitiveTypeTreeName(underlying.MetadataType) : "SInt32";
                if (isElement)
                {
                    align = false;
                }
                nodes.Add(new TypeTreeNode(enumTypeName, name, indent, align));
            }
            else if (CecilUtils.IsGenericList(typeRef))
            {
                var elementRef = CecilUtils.ElementTypeOfCollection(typeRef);
                var elementNodes = TypeRefToTypeTreeNodes(elementRef, "data", indent + 2, true);
                if (elementNodes.Count > 0)
                {
                    nodes.Add(new TypeTreeNode(typeRef.Name, name, indent, align));
                    Helper.AddArray(nodes, indent + 1);
                    nodes.AddRange(elementNodes);
                }
            }
            else if (typeRef is ArrayType arrayType)
            {
                var elementRef = arrayType.ElementType;
                var elementNodes = TypeRefToTypeTreeNodes(elementRef, "data", indent + 2, true);
                if (elementNodes.Count > 0)
                {
                    nodes.Add(new TypeTreeNode(typeRef.Name, name, indent, align));
                    Helper.AddArray(nodes, indent + 1);
                    nodes.AddRange(elementNodes);
                }
            }
            else if (UnityEngineTypePredicates.IsUnityEngineObject(typeRef))
            {
                Helper.AddPPtr(nodes, typeRef.Name, name, indent);
            }
            else if (UnityEngineTypePredicates.IsSerializableUnityClass(typeRef) || UnityEngineTypePredicates.IsSerializableUnityStruct(typeRef))
            {
                if (typeRef.FullName.StartsWith("UnityEngine.LazyLoadReference`1", StringComparison.Ordinal))
                {
                    var targetName = typeRef is GenericInstanceType lazyGeneric && lazyGeneric.GenericArguments.Count == 1
                        ? lazyGeneric.GenericArguments[0].Name
                        : "Object";
                    Helper.AddLazyLoadReference(nodes, targetName, name, indent);
                }
                else
                {
                    switch (typeRef.FullName)
                    {
                        case "UnityEngine.AnimationCurve":
                            Helper.AddAnimationCurve(nodes, name, indent);
                            break;
                        case "UnityEngine.Gradient":
                            Helper.AddGradient(nodes, name, indent);
                            break;
                        case "UnityEngine.GUIStyle":
                            Helper.AddGUIStyle(nodes, name, indent);
                            break;
                        case "UnityEngine.RectOffset":
                            Helper.AddRectOffset(nodes, name, indent);
                            break;
                        case "UnityEngine.Color32":
                            Helper.AddColor32(nodes, name, indent);
                            break;
                        case "UnityEngine.Matrix4x4":
                            Helper.AddMatrix4x4(nodes, name, indent);
                            break;
                        case "UnityEngine.Rendering.SphericalHarmonicsL2":
                            Helper.AddSphericalHarmonicsL2(nodes, name, indent);
                            break;
                        case "UnityEngine.PropertyName":
                            Helper.AddPropertyName(nodes, name, indent);
                            break;
                        case "UnityEngine.Hash128":
                            Helper.AddHash128(nodes, name, indent);
                            break;
                        case "UnityEngine.RenderingLayerMask":
                            Helper.AddRenderingLayerMask(nodes, name, indent);
                            break;
                    }
                }
            }
            else
            {
                var typeDef = typeRef.Resolve();
                if (typeDef == null)
                {
                    nodes.Add(new TypeTreeNode(typeRef.Name, name, indent, align));
                    return nodes;
                }

                var childKey = typeRef.FullName;
                if (State.ActiveTypeCounts.GetValueOrDefault(childKey) >= MaxCycleDepth || indent + 1 > MaxAbsoluteIndentDepth)
                {
                    return nodes;
                }

                nodes.Add(new TypeTreeNode(typeRef.Name, name, indent, align));
                var childResolver = new TypeResolver(typeRef as GenericInstanceType);
                if (typeRef is GenericInstanceType genericInstanceType)
                {
                    childResolver.Add(genericInstanceType);
                }
                var typeDefinitionConverter = new TypeDefinitionConverter(typeDef, childResolver, Helper, indent + 1, State, isRoot: false, activeTypeKey: childKey);
                nodes.AddRange(typeDefinitionConverter.ConvertToTypeTreeNodes());
            }

            return nodes;
        }
    }
}
