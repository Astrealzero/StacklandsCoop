using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StacklandsCoop
{
    /// <summary>
    /// Entry point. The official mod loader instantiates this class and calls Ready().
    /// </summary>
    public sealed class CoopMod : Mod
    {
        public const string ModVersion = "0.4.4";

        public override void Ready()
        {
            CoopLog.Info("StacklandsCoop v" + ModVersion + " Ready()（按 F9 打开/关闭联机面板）");

            try
            {
                Harmony.PatchAll(Assembly.GetExecutingAssembly());
                CoopLog.Info("harmony patches applied");
            }
            catch (Exception ex)
            {
                CoopLog.Error("patch failure: " + ex);
            }

            CoopRuntime.EnsureCreated();
            CoopLog.Info("ready");
        }
    }
}
