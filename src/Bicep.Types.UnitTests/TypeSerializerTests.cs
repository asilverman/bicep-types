// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Bicep.Types.Concrete;
using Azure.Bicep.Types.Serialization;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Azure.Bicep.Types.UnitTests
{
    [TestClass]
    public class TypeSerializerTests
    {
        [TestMethod]
        public void BuiltInType_can_be_serialized_and_deserialized()
        {
            var builtIns = new []
            {
                new BuiltInType(BuiltInTypeKind.Any),
                new BuiltInType(BuiltInTypeKind.Null),
                new BuiltInType(BuiltInTypeKind.Bool),
                new BuiltInType(BuiltInTypeKind.Int),
                new BuiltInType(BuiltInTypeKind.String),
                new BuiltInType(BuiltInTypeKind.Object),
                new BuiltInType(BuiltInTypeKind.Array),
                new BuiltInType(BuiltInTypeKind.ResourceRef),
            };

            using var memoryStream = BuildStream(stream => TypeSerializer.Serialize(stream, builtIns));
            var stream = TypeSerializer.Deserialize(memoryStream);

            for (var i = 0; i < builtIns.Length; i++)
            {
                stream[i].Should().BeOfType<BuiltInType>();
                var deserializedBuiltIn = (BuiltInType)stream[i];

                deserializedBuiltIn.Kind.Should().Be(builtIns[i].Kind);
            }
        }

        class DeferredReference : ITypeReference
        {
            private readonly Func<ITypeReference> typeFunc;

            public DeferredReference(Func<ITypeReference> typeFunc)
            {
                this.typeFunc = typeFunc;
            }

            public TypeBase Type => typeFunc().Type;
        }

        [TestMethod]
        public void Circular_references_are_allowed()
        {
            var factory = new TypeFactory(Enumerable.Empty<TypeBase>());
            ObjectType? typeA = null;
            ObjectType? typeB = null;

            typeA = factory.Create(() => new ObjectType("typeA", new Dictionary<string, ObjectProperty> {
                ["typeB"] = new ObjectProperty(new DeferredReference(() => factory.GetReference(typeB!)), ObjectPropertyFlags.None, "hello!"),
            }, null));
            typeB = factory.Create(() => new ObjectType("typeB", new Dictionary<string, ObjectProperty> {
                ["typeA"] = new ObjectProperty(factory.GetReference(typeA), ObjectPropertyFlags.None, ""),
            }, null));

            using var stream = BuildStream(stream => TypeSerializer.Serialize(stream, factory.GetTypes()));
            var deserialized = TypeSerializer.Deserialize(stream);

            deserialized[0].Should().BeOfType<ObjectType>();
            deserialized[1].Should().BeOfType<ObjectType>();

            var deserializedTypeA = (ObjectType)deserialized[0];
            var deserializedTypeB = (ObjectType)deserialized[1];

            deserializedTypeA.Properties!["typeB"].Type!.Type.Should().Be(deserializedTypeB);
            deserializedTypeB.Properties!["typeA"].Type!.Type.Should().Be(deserializedTypeA);
        }

        [TestMethod]
        public void Different_types_can_be_serialized_and_deserialized()
        {
            var factory = new TypeFactory(Enumerable.Empty<TypeBase>());

            var intType = factory.Create(() => new BuiltInType(BuiltInTypeKind.Int));
            var objectType = factory.Create(() => new ObjectType("steven", new Dictionary<string, ObjectProperty>(), null));
            var arrayType = factory.Create(() => new ArrayType(factory.GetReference(objectType)));
            var resourceType = factory.Create(() => new ResourceType("gerrard", ScopeType.ResourceGroup|ScopeType.Tenant, ScopeType.Tenant, factory.GetReference(objectType), ResourceFlags.None));
            var unionType = factory.Create(() => new UnionType(new [] { factory.GetReference(intType), factory.GetReference(objectType) }));
            var stringLiteralType = factory.Create(() => new StringLiteralType("abcdef"));
            var discriminatedObjectType = factory.Create(() => new DiscriminatedObjectType("disctest", "disctest", new Dictionary<string, ObjectProperty>(), new Dictionary<string, ITypeReference>()));
            var resourceFunctionType = factory.Create(() => new ResourceFunctionType("listTest", "zona", "2020-01-01", factory.GetReference(objectType), factory.GetReference(objectType)));

            using var stream = BuildStream(stream => TypeSerializer.Serialize(stream, factory.GetTypes()));
            var deserialized = TypeSerializer.Deserialize(stream);

            deserialized[0].Should().BeOfType<BuiltInType>();
            deserialized[1].Should().BeOfType<ObjectType>();
            deserialized[2].Should().BeOfType<ArrayType>();
            deserialized[3].Should().BeOfType<ResourceType>();
            deserialized[4].Should().BeOfType<UnionType>();
            deserialized[5].Should().BeOfType<StringLiteralType>();
            deserialized[6].Should().BeOfType<DiscriminatedObjectType>();
            deserialized[7].Should().BeOfType<ResourceFunctionType>();

            ((BuiltInType)deserialized[0]).Kind.Should().Be(intType.Kind);
            ((ObjectType)deserialized[1]).Name.Should().Be(objectType.Name);
            ((ArrayType)deserialized[2]).ItemType!.Type.Should().Be(deserialized[1]);
            ((ResourceType)deserialized[3]).Name.Should().Be(resourceType.Name);
            ((ResourceType)deserialized[3]).Flags.Should().Be(resourceType.Flags);
            ((ResourceType)deserialized[3]).ReadOnlyScopes.HasValue.Should().Be(true);
            ((ResourceType)deserialized[3]).ReadOnlyScopes.Should().Be(resourceType.ReadOnlyScopes);
            ((UnionType)deserialized[4]).Elements![0].Type.Should().Be(deserialized[0]);
            ((UnionType)deserialized[4]).Elements![1].Type.Should().Be(deserialized[1]);
            ((StringLiteralType)deserialized[5]).Value.Should().Be(stringLiteralType.Value);
            ((DiscriminatedObjectType)deserialized[6]).Name.Should().Be(discriminatedObjectType.Name);
            ((ResourceFunctionType)deserialized[7]).Name.Should().Be(resourceFunctionType.Name);
        }

        [TestMethod]
        public void Resources_without_flags_or_readonly_scopes_can_be_deserialized()
        {
            var factory = new TypeFactory(Enumerable.Empty<TypeBase>());
            var objectType = factory.Create(() => new ObjectType("steven", new Dictionary<string, ObjectProperty>(), null));
            var resourceType = factory.Create(() => new ResourceType("gerrard", ScopeType.ResourceGroup|ScopeType.Tenant, ScopeType.Tenant, factory.GetReference(objectType), ResourceFlags.ReadOnly));

            using var stream = BuildStream(stream => TypeSerializer.Serialize(stream, factory.GetTypes()));
            var deserializedNode = JsonSerializer.Deserialize<JsonNode>(stream)!;
            deserializedNode.AsArray()[1]?.AsObject()["4"]?.AsObject().Remove("Flags").Should().BeTrue();
            deserializedNode.AsArray()[1]?.AsObject()["4"]?.AsObject().Remove("ReadOnlyScopes").Should().BeTrue();
            using var rewrittenStream = BuildStream(stream => JsonSerializer.Serialize(stream, deserializedNode));

            var deserialized = TypeSerializer.Deserialize(rewrittenStream);

            deserialized[0].Should().BeOfType<ObjectType>();
            deserialized[1].Should().BeOfType<ResourceType>();

            ((ObjectType)deserialized[0]).Name.Should().Be(objectType.Name);
            ((ResourceType)deserialized[1]).Name.Should().Be(resourceType.Name);
            ((ResourceType)deserialized[1]).Flags.Should().Be(ResourceFlags.None);
            ((ResourceType)deserialized[1]).ReadOnlyScopes.HasValue.Should().Be(false);
        }

        private static Stream BuildStream(Action<Stream> writeFunc)
        {
            var memoryStream = new MemoryStream();
            writeFunc(memoryStream);
            memoryStream.Position = 0;

            return memoryStream;
        }
    }
}
