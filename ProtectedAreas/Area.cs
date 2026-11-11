using System;
using System.Collections.Generic;
using System.Xml.Serialization;
using UnityEngine;

namespace ProtectedAreas
{
    public class AreaMember
    {
        [XmlAttribute] public ulong Id;
        [XmlAttribute] public string Name;
    }

    /// <summary>
    /// Uma area protegida: um cubo alinhado aos eixos, centrado em (X, Y, Z), com "raio" (meia aresta) Radius.
    /// So quem esta em Members pode construir, desmontar, usar ferramentas ou minerar dentro dela.
    /// </summary>
    public class Area
    {
        [XmlAttribute] public string Name;
        [XmlAttribute] public ulong OwnerId;
        [XmlAttribute] public string OwnerName;
        [XmlAttribute] public float X;
        [XmlAttribute] public float Y;
        [XmlAttribute] public float Z;
        [XmlAttribute] public float Radius;

        [XmlElement("Member")]
        public List<AreaMember> Members = new List<AreaMember>();

        /// <summary>Atualizado periodicamente na thread principal; lido tambem por threads do jogo.</summary>
        [XmlIgnore]
        public volatile bool OwnerOnline = true;

        [XmlIgnore]
        public Vector3 Center => new Vector3(X, Y, Z);

        public bool Contains(Vector3 p)
        {
            return Math.Abs(p.x - X) <= Radius
                && Math.Abs(p.y - Y) <= Radius
                && Math.Abs(p.z - Z) <= Radius;
        }

        public bool Intersects(Vector3 center, float radius)
        {
            float r = Radius + radius;
            return Math.Abs(center.x - X) < r
                && Math.Abs(center.y - Y) < r
                && Math.Abs(center.z - Z) < r;
        }

        public bool IsMember(ulong id)
        {
            for (int i = 0; i < Members.Count; i++)
            {
                if (Members[i].Id == id)
                {
                    return true;
                }
            }
            return false;
        }

        public string Describe()
        {
            return $"'{Name}' owner: {OwnerName}, center: ({X:0}, {Y:0}, {Z:0}), radius: {Radius:0}m, authorized: {Members.Count}";
        }
    }

    [XmlRoot("ProtectedAreas")]
    public class AreaFile
    {
        [XmlElement("Area")]
        public List<Area> Areas = new List<Area>();
    }
}
