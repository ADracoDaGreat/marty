﻿using System;
using System.Threading.Tasks;
using Discord;
using Discord.Audio;

namespace Nekobot
{
    class Voice
    {
        internal static AudioService NewService =>
            new AudioService(new AudioServiceConfig
            {
                Mode = AudioMode.Outgoing,
                EnableMultiserver = true,
                EnableEncryption = true,
                Bitrate = 512,
            });

        internal static async Task<DiscordAudioClient> JoinServer(Channel c)
        {
            try { return await Program.client.Audio().Join(c); }
            catch (Exception e)
            {
                Program.log.Error("Voice", "Join Server Error: " + e.Message);
                return null;
            }
        }

        internal static void AddCommands(Commands.CommandGroupBuilder group)
        {

        }
    }
}
