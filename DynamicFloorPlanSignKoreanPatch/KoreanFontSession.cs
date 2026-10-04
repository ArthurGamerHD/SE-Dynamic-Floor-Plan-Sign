using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.Components;
using VRage.Utils;

namespace DynamicFloorPlanSignKoreanPath
{
    /// <summary>
    /// Registers the Korean font of https://steamcommunity.com/sharedfiles/filedetails/?id=1843839106 mod with Dynamic Floor Plan Sign.
    /// The font files are read from that mod as published; The font's bitmaps become the glyph alphamask;
    /// the sign mod supplies the lettering's color-metal and add maps.
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class KoreanFontSession : MySessionComponentBase
    {
        const long CHANNEL = 3811572476L;
        const int PRIORITY = 0;
        const ulong FONT_MOD_ID = 1843839106;
        const string FONT_XML = @"Fonts\white\FontDataPA.xml";
        // Hangul strokes are ~4 px in 44 px cells; thickening the sign's alphamask by 1 px keeps them visible at
        // distance, while 2 px merges strokes in dense syllables. (basically they need to be set to "bold" to be readable)
        const int MASK_DILATION = 1;
        const string CHARACTERS = "3130-318F,AC00-D7A3";

        bool _registered;

        public override void LoadData()
        {
            if (MyAPIGateway.Utilities == null)
                return;

            MyAPIGateway.Utilities.RegisterMessageHandler(CHANNEL, OnModMessage);
            _registered = true;
            SendRegistration();
        }

        protected override void UnloadData()
        {
            if (_registered && MyAPIGateway.Utilities != null)
                MyAPIGateway.Utilities.UnregisterMessageHandler(CHANNEL, OnModMessage);
            _registered = false;
        }

        void OnModMessage(object message)
        {
            if (message is MyTuple<int, string, int, string, MyObjectBuilder_Checkpoint.ModItem>)
                return;

            SendRegistration();
        }

        void SendRegistration()
        {
            MyObjectBuilder_Checkpoint.ModItem fontMod;
            if (!TryGetFontMod(out fontMod))
            {
                MyLog.Default.WriteLineAndConsole("[DynamicFloorPlanSignKoreanPatch] Korean font mod " + FONT_MOD_ID
                    + " is not loaded in this world; no sign font registered");
                return;
            }

            MyAPIGateway.Utilities.SendModMessage(CHANNEL, new MyTuple<int, string, int, string, MyObjectBuilder_Checkpoint.ModItem>(PRIORITY, FONT_XML, MASK_DILATION, CHARACTERS, fontMod));
            MyLog.Default.WriteLineAndConsole("[DynamicFloorPlanSignKoreanPatch] sent Korean sign font registration from " + fontMod.Name);
        }

        static bool TryGetFontMod(out MyObjectBuilder_Checkpoint.ModItem fontMod)
        {
            fontMod = default(MyObjectBuilder_Checkpoint.ModItem);
            if (MyAPIGateway.Session == null || MyAPIGateway.Session.Mods == null)
                return false;

            foreach (MyObjectBuilder_Checkpoint.ModItem mod in MyAPIGateway.Session.Mods)
            {
                if (mod.PublishedFileId == FONT_MOD_ID)
                {
                    fontMod = mod;
                    return true;
                }
            }
            return false;
        }
    }
}
