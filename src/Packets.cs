namespace FsCopilot.Exerciser;

using System.Reflection;
using Network;

/// <summary>
/// The three packet types FS Copilot keeps to itself.
///
/// Codecs.Schema hashes every registered type's assembly-qualified name, and a peer whose
/// hash differs is refused outright, so a harness cannot declare its own Update, SetMaster or
/// InteractCodec - it has to register FS Copilot's. Two of the three are private nested types
/// and the third is internal, which is exactly the reach reflection has and a reference does
/// not.
///
/// Five call sites in total, which is why this is fifteen lines of reflection rather than an
/// InternalsVisibleTo in the application. The alternative - a publicised reference assembly
/// plus IgnoresAccessChecksTo, the way the modding toolchains do it - buys normal syntax at
/// these five sites for the price of a toolchain, and is worth remembering only if this ever
/// reaches much further into the app.
///
/// It fails loudly at startup rather than quietly at join time: a renamed type would
/// otherwise surface as a schema mismatch, which reads as a version skew rather than as this.
/// </summary>
internal static class Packets
{
    private static readonly Type Coordinator = Resolve("FsCopilot.Simulation.Coordinator");
    private static readonly Type MasterSwitch = Resolve("FsCopilot.Simulation.MasterSwitch");

    internal static readonly Type Update = Nested(Coordinator, "Update");
    internal static readonly Type UpdateCodec = Nested(Update, "Codec");
    internal static readonly Type InteractCodec = Nested(Coordinator, "InteractCodec");
    internal static readonly Type SetMaster = Nested(MasterSwitch, "SetMaster");
    internal static readonly Type SetMasterCodec = Nested(SetMaster, "Codec");

    /// <summary>net.RegisterPacket&lt;packet, codec&gt;(), by runtime type.</summary>
    internal static void Register(INetwork net, Type packet, Type codec) =>
        Method(net.GetType(), "RegisterPacket").MakeGenericMethod(packet, codec).Invoke(net, null);

    /// <summary>new SetMaster(peer), sent as net.SendAll&lt;SetMaster&gt;(it).</summary>
    internal static void SendSetMaster(INetwork net, string peer)
    {
        var packet = Activator.CreateInstance(SetMaster, peer)
                     ?? throw new InvalidOperationException("SetMaster would not construct");
        Method(net.GetType(), "SendAll").MakeGenericMethod(SetMaster).Invoke(net, [packet]);
    }

    private static Type Resolve(string fullName) =>
        typeof(INetwork).Assembly.GetType(fullName)
        ?? throw new InvalidOperationException(
            $"{fullName} is not in this FS Copilot build. Re-sync FSC source; if it was renamed there, Packets.cs has to follow.");

    private static Type Nested(Type owner, string name) =>
        owner.GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            $"{owner.Name}.{name} is not in this FS Copilot build. Re-sync FSC source; if it was renamed there, Packets.cs has to follow.");

    private static MethodInfo Method(Type owner, string name) =>
        owner.GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"{owner.Name}.{name} is not in this FS Copilot build.");
}
