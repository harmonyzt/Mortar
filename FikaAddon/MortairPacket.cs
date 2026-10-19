using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Fika.Core.Networking.LiteNetLib.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using UnityEngine;

namespace Mortar.Fika
{
    internal enum PacketKind : byte
    {
        Ready,
        Settings,
        Strike,
        Warning,
        Alert,
        Busy,
        Eye,
        Siren
    }

    internal struct MortairPacket : INetSerializable
    {
        public PacketKind Kind;
        public int NetId;
        public Vector3 Position;
        public string Text;
        public bool Flag;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)Kind);
            writer.Put(NetId);
            writer.Put(Position.x);
            writer.Put(Position.y);
            writer.Put(Position.z);
            writer.Put(Text ?? string.Empty);
            writer.Put(Flag);
        }

        public void Deserialize(NetDataReader reader)
        {
            Kind = (PacketKind)reader.GetByte();
            NetId = reader.GetInt();
            Position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            Text = reader.GetString();
            Flag = reader.GetBool();
        }
    }

    internal static class WireJson
    {
        private static readonly IContractResolver Resolver = new WireContractResolver();

        internal static string Serialize<T>(T value)
        {
            using (var output = new StringWriter(CultureInfo.InvariantCulture))
            using (var writer = new JsonTextWriter(output))
            {
                new JsonSerializer { ContractResolver = Resolver }.Serialize(writer, value);
                
                return output.ToString();
            }
        }

        internal static T Deserialize<T>(string json)
        {
            using (var input = new StringReader(json))
            using (var reader = new JsonTextReader(input))
            {
                return new JsonSerializer { ContractResolver = Resolver }.Deserialize<T>(reader);
            }
        }

        // Keep Unity's recursive vector properties out of this packet serializer only.
        private sealed class WireContractResolver : DefaultContractResolver
        {
            protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization serialization)
            {
                IList<JsonProperty> properties = base.CreateProperties(type, serialization);
                if (type == typeof(Vector2) || type == typeof(Vector3))
                {
                    return properties.Where(property =>
                        property.PropertyName == "x" || property.PropertyName == "y" ||
                        (type == typeof(Vector3) && property.PropertyName == "z")).ToList();
                }

                return properties;
            }
        }
    }
}