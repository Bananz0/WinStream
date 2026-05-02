using System;
using System.Reflection;

var t = Type.GetType("Org.BouncyCastle.Crypto.Parameters.Ed25519+Algorithm, BouncyCastle.Cryptography");
Console.WriteLine(t);
foreach (var v in Enum.GetValues(t)) Console.WriteLine(v);
