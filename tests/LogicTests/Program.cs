using BaoleMaLe;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

var normal = new ActionEffectHandler.Effect { Value = 12345 };
if (CombatTracker.DecodeDamage(normal) != 12345)
    throw new InvalidOperationException("16-bit damage decode failed");

var extended = new ActionEffectHandler.Effect { Value = 12345, Param3 = 2, Param4 = 0x40 };
var expected = 12345u + (2u << 16);
if (CombatTracker.DecodeDamage(extended) != expected)
    throw new InvalidOperationException("extended damage decode failed");

Console.WriteLine("Damage decoding logic tests passed.");
