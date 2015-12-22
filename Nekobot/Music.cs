﻿using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Discord;
using NAudio.Wave;
using TagLib;
using VideoLibrary;
using Nekobot.Commands.Permissions.Levels;

namespace Nekobot
{
    class Music
    {
        class Song
        {
            internal Song(string uri, EType type = EType.Playlist, long requester = 0, string ext = null) { Uri = uri; Type = type; Requester = requester; Ext = ext; }
            internal Song Encore() => new Song(Uri, IsYoutube ? Type : EType.Encore, 0, Ext);

            internal string Title()
            {
                if (Ext == null)
                {
                    File song = File.Create(Uri);
                    if (song.Tag.Title != null && song.Tag.Title != "")
                    {
                        Ext = "";
                        if (song.Tag.Performers != null)
                            foreach (string p in song.Tag.Performers)
                                Ext += $", {p}";
                        if (Ext != "")
                            Ext = Ext.Substring(2) + " **-** ";
                        Ext += song.Tag.Title;
                    }
                    else Ext = System.IO.Path.GetFileNameWithoutExtension(Uri);
                }
                return Ext;
            }
            internal string ExtTitle => $"**[{Type}{(Requester != 0 ? $" by <@{Requester}>" : "")}]** {Title()}";
            internal bool IsYoutube => Type == EType.Youtube;
            internal bool Nonrequested => Type != EType.Playlist;

            internal enum EType { Playlist, Request, Youtube, Encore }
            internal string Uri, Ext;
            internal EType Type;
            internal long Requester;
        }
        // Music-related variables
        internal static string Folder;
        internal static bool UseSubdirs;
        static List<long> streams = new List<long>();
        static Dictionary<long, List<Song>> playlist = new Dictionary<long, List<Song>>();
        static Dictionary<long, bool> skip = new Dictionary<long, bool>();
        static Dictionary<long, bool> reset = new Dictionary<long, bool>();
        internal static Dictionary<long, bool> pause = new Dictionary<long, bool>();
        static Dictionary<long, List<long>> voteskip = new Dictionary<long, List<long>>();
        static Dictionary<long, List<long>> votereset = new Dictionary<long, List<long>>();
        static Dictionary<long, List<long>> voteencore = new Dictionary<long, List<long>>();
        static string[] exts = { ".wma", ".aac", ".mp3", ".m4a", ".wav", ".flac", ".ogg" };

        internal static IEnumerable<string> Files() => System.IO.Directory.EnumerateFiles(Folder, "*.*", UseSubdirs ? System.IO.SearchOption.AllDirectories : System.IO.SearchOption.TopDirectoryOnly).Where(s => exts.Contains(System.IO.Path.GetExtension(s)));
        static bool InPlaylist(List<Song> playlist, string file) => playlist.Exists(song => song.Uri == file);
        static int NonrequestedIndex(Commands.CommandEventArgs e) => 1 + playlist[e.User.VoiceChannel.Id].Skip(1).Where(song => song.Nonrequested).Count();

        static async Task Stream(long cid)
        {
            Channel c = Program.client.GetChannel(cid);
            Discord.Audio.IDiscordVoiceClient _client = await Voice.JoinServer(c);
            Random rnd = new Random();
            if (!playlist.ContainsKey(cid))
                playlist.Add(cid, new List<Song>());
            if (!skip.ContainsKey(cid))
                skip.Add(cid, false);
            if (!reset.ContainsKey(cid))
                reset.Add(cid, false);
            if (!pause.ContainsKey(cid))
                pause.Add(cid, false);
            while (streams.Contains(cid))
            {
                voteskip[cid] = new List<long>();
                votereset[cid] = new List<long>();
                voteencore[cid] = new List<long>();
                var files = Files();
                var filecount = files.Count();
                while (playlist[cid].Count() < (filecount < 11 ? filecount : 11))
                {
                    var mp3 = files.ElementAt(rnd.Next(0, filecount));
                    if (InPlaylist(playlist[cid], mp3))
                        continue;
                    playlist[cid].Add(new Song(mp3));
                }
                await Task.Run(async () =>
                {
                    try
                    {
                        var outFormat = new WaveFormat(48000, 16, 1);
                        int blockSize = outFormat.AverageBytesPerSecond; // 1 second
                        byte[] buffer = new byte[blockSize];
                        string file = playlist[cid][0].Uri;
                        var musicReader = System.IO.Path.GetExtension(file) == ".ogg" ? (IWaveProvider)new NAudio.Vorbis.VorbisWaveReader(file) : new MediaFoundationReader(file);
                        using (var resampler = new MediaFoundationResampler(musicReader, outFormat) { ResamplerQuality = 60 })
                        {
                            int byteCount;
                            while ((byteCount = resampler.Read(buffer, 0, blockSize)) > 0)
                            {
                                if (!streams.Contains(cid) || skip[cid] || reset[cid])
                                {
                                    _client.ClearVoicePCM();
                                    await Task.Delay(1000);
                                    break;
                                }
                                while(pause[cid]);// Play Voice.cs commands in here?
                                _client.SendVoicePCM(buffer, blockSize);
                            }
                        }
                    }
                    catch (OperationCanceledException err) { Console.WriteLine(err.Message); }
                });
                await _client.WaitVoice(); // Prevent endless queueing which would eventually eat up all the ram
                skip[cid] = false;
                if (reset[cid])
                {
                    reset[cid] = false;
                    break;
                }
                playlist[cid].RemoveAt(0);
            }
            await Program.client.LeaveVoiceServer(c.Server);
        }

        internal static Task StartStreams()
        {
            return Task.WhenAll(
              streams.Select(s =>
              {
                  if (Program.client.GetChannel(s).Type == "voice")
                      return Task.Run(() => Stream(s));
                  else
                      return null;
              })
              .Where(t => t != null)
              .ToArray());
        }

        internal static void LoadStreams()
        {
            SQLiteDataReader reader = SQL.ExecuteReader("select channel from flags where music = 1");
            while (reader.Read())
                streams.Add(Convert.ToInt64(reader["channel"].ToString()));
        }

        static async Task ResetStream(long channel)
        {
            reset[channel] = true;
            await Task.Delay(5000);
            await Stream(channel);
        }

        static async Task Encore(Commands.CommandEventArgs e)
        {
            if (await AddVote(voteencore, e, "replay current song", "song will be replayed", "replay"))
            {
                var pl = playlist[e.User.VoiceChannel.Id];
                pl.Insert(1, pl[0].Encore());
            }
        }

        static int CountVoiceChannelMembers(Channel chan)
        {
            if (chan.Type != "voice") return -1;
            return chan.Members.Where(u => u.VoiceChannel == chan).Count()-1;
        }

        static async Task<bool> AddVote(Dictionary<long, List<long>> votes, Commands.CommandEventArgs e, string action, string success, string actionshort)
        {
            var vote = votes[e.User.VoiceChannel.Id];
            if (!vote.Contains(e.User.Id))
            {
                vote.Add(e.User.Id);
                var listeners = CountVoiceChannelMembers(e.User.VoiceChannel);
                if (vote.Count >= Math.Ceiling((decimal)listeners / 2))
                {
                    await Program.client.SendMessage(e.Channel, $"{vote.Count}/{listeners} votes to {action}. 50%+ achieved, {success}...");
                    return true;
                }
                await Program.client.SendMessage(e.Channel, $"{vote.Count}/{listeners} votes to {action}. (Needs 50% or more to {actionshort})");
            }
            return false;
        }

        internal static void AddCommands(Commands.CommandGroupBuilder group)
        {
            group.CreateCommand("playlist")
                .Description("I'll give you the list of songs in the playlist.")
                .FlagMusic(true)
                .Do(async e =>
                {
                    string reply = "";
                    int i = -1;
                    foreach(var t in playlist[e.User.VoiceChannel.Id])
                        reply += (++i == 0) ? $"Currently playing: {t.Title()}.\nNext songs:" : $"\n{i} - {t.ExtTitle}";
                    await Program.client.SendMessage(e.Channel, reply);
                });

            group.CreateCommand("song")
                .Description("I'll tell you the song I'm currently playing.")
                .FlagMusic(true)
                .Do(async e =>
                {
                    await Program.client.SendMessage(e.Channel, $"Currently playing: {playlist[e.User.VoiceChannel.Id][0].Title()}.");
                });

            group.CreateCommand("ytrequest")
                .Parameter("youtube video link(s)", Commands.ParameterType.Unparsed)
                .Description("I'll add youtube videos to the playlist")
                .FlagMusic(true)
                .Do(async e =>
                {
                    MatchCollection m = Regex.Matches(e.Args[0], @"youtu(?:be\.com\/(?:v\/|e(?:mbed)?\/|watch\?v=)|\.be\/)([\w-_]{11}\b)", RegexOptions.IgnoreCase);
                    foreach (Match match in m)
                    {
                        var link = $"youtube.com/watch?v={match.Groups[1]}";
                        var video = await YouTube.Default.GetVideoAsync(link);
                        var pl = playlist[e.User.VoiceChannel.Id];
                        string uri;
                        try { uri = video.Uri; }
                        catch
                        {
                            Program.rclient.BaseUrl = new Uri("http://www.youtubeinmp3.com/fetch/");
                            uri = Newtonsoft.Json.Linq.JObject.Parse(Program.rclient.Execute(new RestSharp.RestRequest($"?format=JSON&video={System.Net.WebUtility.UrlEncode(link)}", RestSharp.Method.GET)).Content)["link"].ToString();
                        }
                        var ext = $"{video.Title} ({link})";
                        if (pl.Exists(song => song.Ext == ext))
                            await Program.client.SendMessage(e.Channel, $"<@{e.User.Id}> Your request ({video.Title}) is already in the playlist.");
                        else
                        {
                            pl.Insert(NonrequestedIndex(e), new Song(uri, Song.EType.Youtube, e.User.Id, ext));
                            await Program.client.SendMessage(e.Channel, $"{video.Title} added to the playlist.");
                        }
                    }
                    if (m.Count == 0)
                        await Program.client.SendMessage(e.Channel, $"None of {e.Args[0]} could be added to playlist because no valid youtube links were found within.");
                });

            group.CreateCommand("request")
                .Parameter("song to find", Commands.ParameterType.Required)
                .Parameter("...", Commands.ParameterType.Multiple)
                .Description("I'll try to add your request to the playlist!")
                .FlagMusic(true)
                .Do(async e =>
                {
                    foreach (var file in Files())
                    {
                        if (System.IO.Path.GetFileNameWithoutExtension(file).ToLower().Contains(string.Join(" ", e.Args).ToLower()))
                        {
                            var pl = playlist[e.User.VoiceChannel.Id];
                            var i = NonrequestedIndex(e);
                            var cur_i = pl.FindIndex(song => song.Uri == file);
                            if (cur_i != -1)
                            {
                                if (i > cur_i)
                                {
                                    if (cur_i == 0)
                                        await Encore(e);
                                    else
                                        await Program.client.SendMessage(e.Channel, $"<@{e.User.Id}> Your request is already in the playlist at {cur_i}.");
                                    return;
                                }
                                pl.RemoveAt(cur_i);
                            }
                            pl.Insert(i, new Song(file, Song.EType.Request, e.User.Id));
                            await Program.client.SendMessage(e.Channel, $"<@{e.User.Id}> Your request has been added to the list.");
                            return;
                        }
                    }
                    await Program.client.SendMessage(e.Channel, $"<@{e.User.Id}> Your request was not found.");
                });

            group.CreateCommand("skip")
                .Description("Vote to skip the current song. (Will skip at 50% or more)")
                .FlagMusic(true)
                .Do(async e =>
                {
                    if (await AddVote(voteskip, e, "skip current song", "skipping song", "skip"))
                       skip[e.User.VoiceChannel.Id] = true;
                });

            group.CreateCommand("reset")
                .Description("Vote to reset the stream. (Will reset at 50% or more)")
                .FlagMusic(true)
                .Do(async e =>
                {
                    if (await AddVote(votereset, e, "reset the stream", "resetting stream", "reset"))
                        await ResetStream(e.User.VoiceChannel.Id);
                });

            group.CreateCommand("encore")
                .Alias("replay")
                .Alias("ankoru")
                .Description("Vote to replay the current song. (Will replay at 50% or more)")
                .FlagMusic(true)
                .Do(async e => await Encore(e));

            // Moderator commands
            group.CreateCommand("forceskip")
                .MinPermissions(1)
                .FlagMusic(true)
                .Description("I'll skip the currently playing song.")
                .Do(async e =>
                {
                    skip[e.User.VoiceChannel.Id] = true;
                    await Program.client.SendMessage(e.Channel, "Forcefully skipping song...");
                });

            group.CreateCommand("forcereset")
                .MinPermissions(1)
                .FlagMusic(true)
                .Description("I'll reset the stream in case of bugs, while keeping the playlist intact.")
                .Do(async e =>
                {
                    await Program.client.SendMessage(e.Channel, "Reseting stream...");
                    await ResetStream(e.User.VoiceChannel.Id);
                });

            group.CreateCommand("pause")
                .Alias("unpause")
                .MinPermissions(1)
                .FlagMusic(true)
                .Description("I'll toggle pause on the stream")
                .Do(async e =>
                {
                    await Program.client.SendMessage(e.Channel, $"{(pause[e.User.VoiceChannel.Id] ? "Resum" : "Paus")}ing stream...");
                    pause[e.User.VoiceChannel.Id] = !pause[e.User.VoiceChannel.Id];
                });

            // Administrator commands
            group.CreateCommand("music")
                .Parameter("on/off", Commands.ParameterType.Required)
                .Description("I'll start or end a stream in a particular voice channel, which you need to be in.")
                .MinPermissions(2)
                .Do(async e =>
                {
                    if (e.User.VoiceChannel?.Id <= 0)
                        await Program.client.SendMessage(e.Channel, $"<@{e.User.Id}>, you need to be in a voice channel to use this.");
                    else
                    {
                        bool on = e.Args[0] == "on";
                        bool off = !on && e.Args[0] == "off";
                        if (on || off)
                        {
                            bool has_stream = streams.Contains(e.User.VoiceChannel.Id);
                            string status = on ? "start" : "halt";
                            if (has_stream == on || has_stream != off)
                            {
                                string blah = on ? "streaming in! Did you mean to !reset or !forcereset the stream?" : "not streaming in!";
                                await Program.client.SendMessage(e.Channel, $"<@{e.User.Id}>, I can't {status} streaming in a channel that I'm already {blah}");
                            }
                            else
                            {
                                SQL.ExecuteNonQuery(off ? $"update flags set music=0 where channel='{e.User.VoiceChannel.Id}'"
                                    : SQL.ExecuteScalarPos($"select count(channel) from flags where channel = '{e.User.VoiceChannel.Id}'")
                                    ? $"update flags set music=1 where channel='{e.User.VoiceChannel.Id}'"
                                    : $"insert into flags values ('{e.User.VoiceChannel.Id}', 0, 1, 0, -1)");
                                await Program.client.SendMessage(e.Channel, $"<@{e.User.Id}>, I'm {status}ing the stream!");
                                if (on)
                                {
                                    streams.Add(e.User.VoiceChannel.Id);
                                    await Stream(e.User.VoiceChannel.Id);
                                }
                                else streams.Remove(e.User.VoiceChannel.Id);
                            }
                        }
                        else await Program.client.SendMessage(e.Channel, $"<@{e.User.Id}>, the argument needs to be either on or off.");
                    }
                });
        }
    }
}
