﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Discord;
using NAudio.Wave;
using File = TagLib.File;
using VideoLibrary;
using Nekobot.Commands.Permissions.Levels;
using System.IO;

namespace Nekobot
{
    class Music
    {
        class Song
        {
            internal Song(string uri, EType type = EType.Playlist, User requester = null, string ext = null) { Uri = uri; Type = type; Requester = requester?.Mention; Ext = ext; }
            Song Clone(EType type) => new Song(Uri, type, ext: Ext);
            internal Song Encore() => Clone(IsOnline ? Type : EType.Encore);
            internal Song Repeat() => Clone(IsOnline ? EType.RepeatOnline : EType.Repeat);

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
                    else Ext = Path.GetFileNameWithoutExtension(Uri);
                }
                return Ext;
            }
            internal string ExtTitle => $"**[{Type}{(Requester != null ? $" by {Requester}" : "")}]** {Title()}";
            internal bool IsOnline => Type == EType.RepeatOnline || Type == EType.Youtube || Type == EType.SoundCloud;
            internal bool Nonrequested => Type >= EType.Request;

            internal enum EType { Playlist, Repeat, RepeatOnline, Request, Youtube, SoundCloud, Encore }
            internal string Uri, Requester, Ext;
            EType Type;
        }
        class Playlist : List<Song>
        {
            internal void Initialize()
            {
                if (!HasFolder()) return;
                lock (this)
                {
                    var files = Files(Folder);
                    var filecount = files.Count();
                    var maxfiles = Math.Min(filecount, 11);
                    Random rnd = new Random();
                    while (Count < maxfiles)
                    {
                        var mp3 = files.ElementAt(rnd.Next(0, filecount));
                        if (InPlaylist(mp3))
                            continue;
                        Add(new Song(mp3));
                    }
                }
            }
            // Return true if we're resetting
            internal bool Cleanup()
            {
                foreach (var vote in votes)
                    lock (vote) vote.Clear();
                if (exit)
                {
                    exit = skip = false;
                    return true;
                }
                lock (this)
                {
                    if (Any)
                    {
                        // If skip, they want us to remove the current song from the repeat queue.
                        if (!skip && repeat) Add(this[0].Repeat());
                        RemoveAt(0);
                    }
                    skip = false;
                }
                return false;
            }

            internal void SkipRange(int index = 0, int count = 1)
            {
                if (index == 0)
                {
                    if (count >= Count)
                    {
                        SkipAll();
                        return;
                    }
                    // Skip current song.
                    lock (this) skip = true;
                    ++index;
                    --count;
                }
                if (count <= 0 || index >= Count)
                    return;
                if (index+count >= Count) // Clamp to remove from index to end.
                    count = Count-index;
                lock (this)
                    RemoveRange(index, count);
            }

            internal void SkipSongs(int count = 1) => SkipRange(count: count);
            internal void SkipLastSongs(int count = 1) => SkipRange(Count - count, count);

            internal void SkipAll()
            {
                lock(this)
                {
                    Clear();
                    skip = true; // Skip current song.
                }
            }

            #region Information
            bool Any => this.Any();

            bool InPlaylist(string common, bool online = false) => Exists(song => (song.IsOnline == online) && (online ? song.Ext : song.Uri) == common);

            int NonrequestedIndex() => Any ? 1 + this.Skip(1).Where(song => song.Nonrequested).Count() : 0;

            internal async Task<string> CurrentUri(Action play_gestures)
            {
                while (!Any)
                {
                    if (exit) return null;
                    play_gestures();
                    await Task.Delay(1000);
                }
                lock (this) return this[0].Uri;
            }

            string EmptyPlaylist() =>
                Any ? null : "The playlist is currently empty, use commands to request something.";

            internal string CurrentSong()
            {
                lock(this)
                    return EmptyPlaylist() ?? $"Currently playing: {this[0].Title()}.";
            }

            internal string SongCount()
            {
                lock (this)
                    return EmptyPlaylist() ?? $"There are currently {Count} songs left in the playlist.";
            }

            internal string SongList()
            {
                lock (this)
                {
                    string reply = EmptyPlaylist() ?? "";
                    if (reply != "") return reply;
                    string padding = Helpers.ZeroPadding(Count);
                    int i = -1;
                    foreach(var t in this)
                    {
                        reply += (++i == 0) ? $"Currently playing: {t.Title()}.{(Count > 1 ? "\nNext songs:" : "")}" : $"\n{Helpers.ZeroPaddingAt(i, ref padding)}{i} - {t.ExtTitle}";
                        if (reply.Length > 2000) return reply.Substring(0, reply.LastIndexOf('\n'));
                    }
                    return reply;
                }
            }
            #endregion

            #region Insert Wrappers
            internal void InsertFile(string file, Commands.CommandEventArgs e)
            {
                lock (this)
                {
                    var i = NonrequestedIndex();
                    var cur_i = FindIndex(song => song.Uri == file);
                    if (cur_i != -1)
                    {
                        if (i > cur_i)
                        {
                            if (cur_i == 0)
                            {
                                if (EncoreVote(e)) InsertEncore();
                            }
                            else
                                e.Channel.SendMessage($"{e.User.Mention} Your request is already in the playlist at {cur_i}.");
                            return;
                        }
                        RemoveAt(cur_i);
                    }
                    Insert(i, new Song(file, Song.EType.Request, e.User));
                }
            }

            internal bool TryInsert(Song song)
            {
                lock (this)
                if (!InPlaylist(song.Ext, true))
                {
                    Insert(NonrequestedIndex(), song);
                    return true;
                }
                return false;
            }

            void InsertEncore() => Insert(1, this[0].Encore());
            #endregion

            #region Votes and actions
            internal bool AddVote(List<ulong> vote, Commands.CommandEventArgs e, string action, string success, string actionshort)
            {
                lock (vote)
                if (!vote.Contains(e.User.Id))
                {
                    vote.Add(e.User.Id);
                    var listeners = e.User.VoiceChannel.Users.Count() - 1;
                    var needed = Math.Ceiling((float)listeners / 2);
                    if (vote.Count == needed)
                    {
                        e.Channel.SendMessage($"{vote.Count}/{listeners} votes to {action}. 50%+ achieved, {success}...");
                        return true;
                    }
                    else if (vote.Count < needed)
                        e.Channel.SendMessage($"{vote.Count}/{listeners} votes to {action}. (Needs 50% or more to {actionshort})");
                }
                return false;
            }

            internal void ChannelUpdated(ChannelUpdatedEventArgs e)
            {
                var listeners = e.After.Users;
                var listeners_count = listeners.Count();
                if (e.Before.Users.Count() <= listeners_count) return; // If we've less listeners, a vote might have passed.
                var needed = Math.Ceiling((float)(listeners_count-1) / 2);
                for (int i = 0; i < votes.Length; ++i)
                {
                    var vote = votes[i];
                    if (vote.Count >= needed) continue; // This may already be fulfilled, if so, don't bother.
                    vote = vote.Where(id => listeners.Any(u => u.Id == id)).ToList();
                    if (vote.Count >= needed)
                    {
                        switch(i)
                        {
                            case (int)Vote.Encore: DoEncore(); break;
                            case (int)Vote.Reset: Task.Run(() => streams.Reset(e.After)); break;
                            case (int)Vote.Skip: SkipSongs(); break;
                        }
                    }
                }
            }

            bool EncoreVote(Commands.CommandEventArgs e)
                => AddVote(votes[(int)Vote.Encore], e, "replay current song", "song will be replayed", "replay");

            void DoEncore() { lock (this) InsertEncore(); }
            internal void Encore(Commands.CommandEventArgs e)
            {
                if (EncoreVote(e))
                    DoEncore();
            }

            internal void Skip(Commands.CommandEventArgs e)
            {
                if (!skip && AddVote(votes[(int)Vote.Skip], e, "skip current song", "skipping song", "skip"))
                    SkipSongs();
            }
            internal async Task Reset(Commands.CommandEventArgs e)
            {
                if (!exit && AddVote(votes[(int)Vote.Reset], e, "reset the stream", "resetting stream", "reset"))
                    await streams.Reset(e.User.VoiceChannel);
            }
            internal void Pause(Commands.CommandEventArgs e)
            {
                e.Channel.SendMessage($"{(pause ? "Resum" : "Paus")}ing stream...");
                pause = !pause;
            }
            internal void Repeat(Commands.CommandEventArgs e)
            {
                e.Channel.SendMessage($"Turning {(repeat ? "off" : "on")} repeat mode for stream...");
                repeat = !repeat;
            }
            internal void Exit()
            {
                lock (this)
                {
                    pause = false;
                    exit = true;
                }
            }
            #endregion

            enum Vote { Skip, Reset, Encore }
            List<ulong>[] votes = { new List<ulong>(), new List<ulong>(), new List<ulong>() };
            internal bool skip = false, exit = false, pause = false, repeat = false;
        }

        class Stream
        {
            internal Stream(Channel chan, bool request) { Channel = chan; _request = request; }

            static WaveStream Reader(string file)
            {
                for (byte i = 3; i != 0; --i) try
                {
                    return Path.GetExtension(file) == ".ogg"
                            ? (WaveStream)new NAudio.Vorbis.VorbisWaveReader(file)
                            : new MediaFoundationReader(file);
                } catch { }
                return null;
            }

            internal void PlayUri(Discord.Audio.IAudioClient _client, string uri, Func<bool> cancel = null)
            {
                var musicReader = Reader(uri);
                if (musicReader == null)
                {
                    Program.log.Warning("Stream", $"{uri} couldn't be read.");
                    return;
                }
                var channels = Program.Audio.Config.Channels;
                var outFormat = new WaveFormat(48000, 16, channels);
                using (var resampler = new MediaFoundationResampler(musicReader, outFormat) { ResamplerQuality = 60 })
                {
                    int blockSize = outFormat.AverageBytesPerSecond; // 1 second
                    byte[] buffer = new byte[blockSize];
                    while (cancel == null || !cancel())
                    {
                        bool end = musicReader.Position+blockSize > musicReader.Length; // Stop at the end, work around the bug that has it Read twice.
                        if (resampler.Read(buffer, 0, blockSize) <= 0) break; // Break on failed read.
                        _client.Send(buffer, 0, blockSize);
                        if (end) break;
                    }
                }
                musicReader.Dispose();
            }

            internal async Task Play()
            {
                ulong cid = Channel.Id;
                var _client = await Voice.JoinServer(Channel);
                if (!playlist.ContainsKey(cid))
                    playlist.Add(cid, new Playlist());
                var pl = Playlist;
                Action play_gestures = () =>
                {
                    while (_gestures.Count != 0)
                    {
                        lock (_gestures)
                        {
                            var gesture = _gestures[0];
                            _gestures.RemoveAt(0);
                            PlayUri(_client, gesture.Uri);
                            Playlist.pause = gesture.Paused;
                        }
                    }
                };
                while (streams.Contains(this))
                {
                    if (!_request) pl.Initialize();
                    await Task.Run(async () =>
                    {
                        try
                        {
                            string uri = await pl.CurrentUri(play_gestures);
                            if (uri == null) return; // Only happens if we're done here.
                            PlayUri(_client, uri, () =>
                            {
                                while (pl.pause)
                                {
                                    System.Threading.Thread.Sleep(500);
                                    play_gestures();
                                }
                                if (!streams.Contains(this) || pl.skip || pl.exit)
                                {
                                    _client.Clear();
                                    System.Threading.Thread.Sleep(1000);
                                    return true;
                                }
                                return false;
                            });
                        }
                        catch (OperationCanceledException err) { Program.log.Error("Stream", err.Message); }
                    });
                    _client.Wait(); // Prevent endless queueing which would eventually eat up all the ram
                    if (pl.Cleanup()) break;
                }
                await Program.Audio.Leave(Channel.Server);
            }

            internal void Stop()
            {
                SQL.AddOrUpdateFlag(Channel.Id, "music", "0");
                playlist[Channel.Id].Exit();
                streams.Remove(this);
            }

            internal void QueueGesture(string uri)
            {
                lock (_gestures)
                {
                    _gestures.Add(new Gesture(uri, Playlist.pause));
                    Playlist.pause = true;
                }
            }

            internal Channel Channel;
            internal Server Server => Channel.Server;
            Playlist Playlist => playlist[Channel.Id];
            class Gesture
            {
                internal Gesture(string uri, bool paused) { Uri = uri; Paused = paused; }
                internal string Uri;
                internal bool Paused;
            }
            List<Gesture> _gestures = new List<Gesture>();

            private bool _request;
            internal bool Request
            {
                get { return _request; }
                set
                {
                    if (_request = value)
                        Playlist.SkipAll();
                    else
                        Playlist.Initialize();
                }
            }
        }

        class Streams : List<Stream>
        {
            internal bool Has(Channel c) => this.Any(s => c == s.Channel);
            internal Stream Get(Channel c) => this.FirstOrDefault(s => c == s.Channel);

            internal async Task AddStream(Channel c, bool request = false)
            {
                var stream = new Stream(c, request);
                Add(stream);
                await stream.Play();
            }

            internal Task Load(DiscordClient client)
            {
                // Load the stream channels
                var channels = new List<Tuple<Channel,int>>();
                var reader = SQL.ReadChannels("music<>0", "channel,music");
                while (reader.Read())
                    channels.Add(Tuple.Create(client.GetChannel(Convert.ToUInt64(reader["channel"].ToString())), int.Parse(reader["music"].ToString())));
                return Task.WhenAll(
                  channels.Select(s => s.Item1?.Type == ChannelType.Voice ? Task.Run(async() => await AddStream(s.Item1, s.Item2 == 2)) : null)
                  .Where(t => t != null).ToArray());
            }

            internal async Task Play(Commands.CommandEventArgs e, bool request, Stream stream = null)
            {
                SQL.AddOrUpdateFlag(e.User.VoiceChannel.Id, "music", request ? "2" : "1");
                if (stream != null)
                    stream.Request = request;
                else
                {
                    await streams.Stop(e.Server);
                    await streams.AddStream(e.User.VoiceChannel, request);
                }
            }

            internal async Task Stop(Server server)
            {
                var serverstreams = this.Where(stream => server == stream.Server).ToArray();
                foreach (var stream in serverstreams)
                    stream.Stop();
                if (serverstreams.Length != 0)
                    await Task.Delay(5000);
            }

            internal async Task Reset(Channel c)
            {
                playlist[c.Id].Exit();
                await Task.Delay(7500);
                await streams.First(s => s.Channel == c).Play(); // If this throws, something has gone horribly wrong.
            }
        }

        internal static bool Get(Channel c) => c != null && streams.Has(c);
        internal static void Load(DiscordClient c)
        {
            streams.Load(c);
            c.ChannelUpdated += (s, e) =>
            {
                if (e.Before.Type == ChannelType.Voice && playlist.ContainsKey(e.Before.Id))
                    playlist[e.Before.Id].ChannelUpdated(e);
            };
        }
        internal static async Task Stop(Server s) => await streams.Stop(s);

        // Music-related variables
        internal static string Folder;
        internal static bool UseSubdirs;
        static Streams streams = new Streams();
        static Dictionary<ulong, Playlist> playlist = new Dictionary<ulong, Playlist>();

        static bool HasFolder() => Folder.Length != 0;
        static IEnumerable<string> Files(string folder) => Directory.EnumerateFiles(folder, "*.*", UseSubdirs ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Where(s => new []{ ".wma", ".aac", ".mp3", ".m4a", ".wav", ".flac", ".ogg" }.Contains(Path.GetExtension(s)));
        //static IEnumerable<string> PlaylistFiles(string folder) => Directory.EnumerateFiles(folder, "*.*", UseSubdirs ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Where(s => new[]{".pls"}.Contains(Path.GetExtension(s)));

        static Commands.CommandBuilder CreatePLCmd(Commands.CommandGroupBuilder group, string name, string description, string[] aliases = null)
        {
            var cmd = group.CreateCommand(name)
                .Description(description)
                .FlagMusic(true);
            if (aliases != null) foreach (var alias in aliases) cmd.Alias(alias);
            return cmd;
        }
        static Commands.CommandBuilder CreatePLCmd(Commands.CommandGroupBuilder group, string name, string parameter, string description, string alias) => CreatePLCmd(group, name, parameter, description, new[]{alias});
        static Commands.CommandBuilder CreatePLCmd(Commands.CommandGroupBuilder group, string name, string parameter, string description, string[] aliases = null) => CreatePLCmd(group, name, description, aliases).Parameter(parameter, Commands.ParameterType.Unparsed);

        class SC : SoundCloud.NET.SoundCloudManager
        {
            public SC(string clientId, string userAgent = "") : base(clientId, userAgent) { }
            bool Triad(Commands.CommandEventArgs e, SoundCloud.NET.Models.Track track, bool multiple, bool say_added = false)
            {
                var pl = playlist[e.User.VoiceChannel.Id];
                var title = $"{track.Title} by {track.User.Username}";
                if (!track.Streamable)
                {
                    if (multiple) e.Channel.SendMessage($"{title} is not streamable.");
                    return false;
                }
                var ext = $"{title} (**{track.PermalinkUrl}**)";
                if (pl.TryInsert(new Song($"{track.StreamUrl}?client_id={ClientID}", Song.EType.SoundCloud, e.User, ext)))
                {
                    if (!say_added) e.Channel.SendMessage($"{title} added to the playlist.");
                    return true;
                }
                if (multiple)
                    e.Channel.SendMessage($"{title} is already in the playlist.");
                return false;
            }
            int Triad(Commands.CommandEventArgs e, SoundCloud.NET.Models.Playlist playlist, bool multiple, bool say_added = false)
            {
                int ret = 0; // playlist.TrackCount includes unplayable tracks.
                foreach (var track in playlist.Tracks)
                    if (Triad(e, track, false, true)) ++ret;
                if (!say_added)
                {
                    if (ret != 0)
                        e.Channel.SendMessage($"The contents of {playlist.Title} by {playlist.User.Username} ({ret} tracks) have been added to the playlist.");
                    else if (!multiple)
                        e.Channel.SendMessage($"There is nothing in {playlist.Title} that isn't already in the playlist.");
                }
                return ret;
            }

            public void CreatePermalinkCmd(Commands.CommandGroupBuilder group, string name, string alias, bool is_playlist) => CreatePermalinkCmd(group, name, new[]{alias}, is_playlist);
            public void CreatePermalinkCmd(Commands.CommandGroupBuilder group, string name, string[] aliases, bool is_playlist)
            {
                CreatePLCmd(group, name, $"SoundCloud {(!is_playlist ? "Track" : "Playlist")} Permalink(s)", $"I'll add SoundCloud {(is_playlist ? "playlist " : "")}songs to the playlist!", aliases)
                    .Do(e =>
                    {
                        // TODO: Find out if snd.sc links for playlists exist, it's doubtful since it's not exposed in their ui.
                        MatchCollection m = Regex.Matches(e.Args[0].Replace(' ', '\n'), $@"^https?:\/\/(?:soundcloud\.com({(is_playlist ? "?:" : "?!")}\/.+\/sets){(is_playlist ? "" : @"|snd\.sc")})\/.+$", RegexOptions.IgnoreCase|RegexOptions.Multiline);
                        if (m.Count == 0)
                            e.Channel.SendMessage($"{e.User.Mention} No SoundCloud {(is_playlist ? "playlist" : "track")} permalink matches.");
                        else foreach (var link in from Match match in m select match.Groups[0].ToString())
                            if (is_playlist) Triad(e, GetPlaylist(link), false);
                            else Triad(e, GetTrack(link), true);
                    });
            }

            public enum SearchType
            {
                Simple,
                Multiple,
                Random
            }
            private SoundCloud.NET.Models.BaseModel[] Search(Commands.CommandEventArgs e, bool is_playlist, int desired = 0)
            {
                var search = new SoundCloud.NET.SearchParameters(string.Join(" ", desired != 0 ? e.Args.Skip(1) : e.Args)){ Limit = 200, Streamable = true };
                var container = is_playlist ? (SoundCloud.NET.Models.BaseModel[])SearchPlaylist(search) : SearchTrack(search, desired != 0 ? desired : 500);
                if (container.Count() == 0)
                {
                    e.Channel.SendMessage($"{e.User.Mention} Your request was not found.");
                    return null;
                }
                return container;
            }
            public void CreateSearchCmd(Commands.CommandGroupBuilder group, string name, string alias, bool is_playlist, SearchType st = SearchType.Simple) => CreateSearchCmd(group, name, new[]{alias}, is_playlist, st);
            public void CreateSearchCmd(Commands.CommandGroupBuilder group, string name, string[] aliases, bool is_playlist, SearchType st = SearchType.Simple)
            {
                const int random_tries = 10; // how many times to retry random selection.
                bool multiple = st == SearchType.Multiple;
                var cmd = CreatePLCmd(group, name,
                    $"I'll search for your {(is_playlist ? "playlist " : "")}request on SoundCloud!\nResults will be considered {(st == SearchType.Random ? $"{random_tries} times at random until one is found that isn't already in the playlist." : $"in order until {(!multiple ? "one not in the playlist is found" : "the amount of count or all (that are not already in the playlist) have been added")}")}.", aliases);
                if (multiple) cmd.Parameter("count", Commands.ParameterType.Optional);
                // Until we can figure out how to include Playlist search terms without getting gateway errors, RIP.
                if (!is_playlist) cmd.Parameter("keywords", Commands.ParameterType.Unparsed);
                cmd.Do(e => st == SearchType.Random ? Task.Run(() =>
                    {
                        var container = Search(e, is_playlist);
                        if (container == null) return;
                        var r = new Random();
                        for (int i = random_tries; i != 0; --i)
                        {
                            var thing = container[r.Next(container.Length)];
                            if (is_playlist ? Triad(e, (SoundCloud.NET.Models.Playlist)thing, true) != 0 : Triad(e, (SoundCloud.NET.Models.Track)thing, false)) return;
                        }
                        e.Channel.SendMessage($"No new tracks found, tried {random_tries} times.");
                    })
                    : Task.Run(() =>
                    {
                        int max = 0;
                        if (multiple && Helpers.HasArg(e.Args) && !int.TryParse(e.Args[0], out max))
                            max = 100;
                        var container = Search(e, is_playlist, max);
                        if (container == null) return;
                        int count = 0;
                        int trackcount = 0;
                        foreach (var thing in container)
                        {
                            var tc = is_playlist ? Triad(e, (SoundCloud.NET.Models.Playlist)thing, true, multiple) : Triad(e, (SoundCloud.NET.Models.Track)thing, false, multiple) ? 1 : 0;
                            if (tc != 0)
                            {
                                if (!multiple) return;
                                if (is_playlist) trackcount += tc;
                                if (++count == max) break;
                            }
                        }
                        e.Channel.SendMessage(multiple && count != 0 ? $"{count} {(is_playlist ? $"playlists (totaling {trackcount} tracks)" : "tracks")} added." : $"{e.User.Mention} No results for your requested search aren't already in the playlist.");
                    }));
            }
        }

        internal static class YT
        {
            static RestSharp.RestClient rclient = Helpers.GetRestClient("http://www.youtubeinmp3.com/fetch/");
            public static Regex regex = new Regex(@"youtu(?:be\.com\/(?:v\/|e(?:mbed)?\/|watch\?v=)|\.be\/)([\w-_]{11}\b)", RegexOptions.IgnoreCase);

            public class VideoData
            {
                public VideoData(string u, string t, string l) { Uri = u; Title = t; Link = l; }
                public string Uri, Title, Link;
                public static VideoData Get(string link)
                {
                    try { var video = YouTube.Default.GetVideo(link); return new VideoData(video.Uri, video.Title, link); }
                    catch
                    {
                        // TODO: Content is sometimes an html page instead of JSON, we should ask why.
                        var json = Newtonsoft.Json.Linq.JObject.Parse(rclient.Execute(new RestSharp.RestRequest($"?format=JSON&video={System.Net.WebUtility.UrlEncode(link)}", RestSharp.Method.GET)).Content);
                        return new VideoData(json["link"].ToString(), json["title"].ToString(), link);
                    }
                }
            }

            static bool Triad(Commands.CommandEventArgs e, VideoData video)
            {
                bool ret = playlist[e.User.VoiceChannel.Id].TryInsert(new Song(video.Uri, Song.EType.Youtube, e.User, video.Title + (video.Link == null ? "" : $" ({video.Link})")));
                e.Channel.SendMessage(ret ? $"{video.Title} added to the playlist."
                        : $"{e.User.Mention} Your request ({video.Title}) is already in the playlist.");
                return ret;
            }

            static public void CreateCommand(Commands.CommandGroupBuilder group, string name, string alias) => CreateCommand(group, name, new[]{alias});
            static public void CreateCommand(Commands.CommandGroupBuilder group, string name, string[] aliases = null)
            {
                CreatePLCmd(group, name, $"youtube video link(s)", $"I'll add youtube videos to the playlist", aliases)
                    .Do(e =>
                    {
                        MatchCollection m = regex.Matches(e.Args[0]);
                        if (m.Count == 0)
                            e.Channel.SendMessage($"None of {e.Args[0]} could be added to playlist because no valid youtube links were found within.");
                        else foreach (var link in from Match match in m select $"youtube.com/watch?v={match.Groups[1]}")
                            Triad(e, VideoData.Get(link));
                    });
            }
        }

        static class Local
        {
            public static void CreateCommand(Commands.CommandGroupBuilder group, string name, bool all/*, bool is_playlist*/)
            {
                CreatePLCmd(group, name, $"{(/*is_playlist ? "playlist" :*/ "song")}{(all ? "s" : "")} to find", $"I'll try to add {(all ? "all songs matching " : "")}your request to the playlist!")
                    .Do(e =>
                    {
                        var args = string.Join(" ", e.Args);
                        if (args.Length == 0)
                        {
                            e.Channel.SendMessage("You need to provide at least a character to search for.");
                            return;
                        }
                        args = args.ToLower();
                        Func<string, bool> search = f => Path.GetFileNameWithoutExtension(f).ToLower().Contains(args);
                        long filecount = 0;
                        var pl = playlist[e.User.VoiceChannel.Id];
                        /*var insert_file = is_playlist ? (Action<string>)(file =>
                            {
                                for ()
                            }) :
                            file => pl.InsertFile(file, e);*/
                        Action<string> insert_file = file => pl.InsertFile(file, e);
                        var songs = /*is_playlist ? PlaylistFiles(Folder) :*/ Files(Folder);
                        if (all)
                        {
                            var files = songs.Where(search);
                            foreach (var file in files)
                                insert_file(file);
                            filecount = files.Count();
                        }
                        else
                        {
                            var file = songs.FirstOrDefault(search);
                            if (file != null)
                            {
                                insert_file(file);
                                filecount = 1;
                            }
                        }

                        e.Channel.SendMessage($"{e.User.Mention} {(filecount > 1 ? filecount.ToString() : "Your")} request{(filecount != 0 ? $"{(filecount == 1 ? " has" : "s have")} been added to the list" : " was not found")}.");
                    });
            }
        }

        internal static void AddCommands(Commands.CommandGroupBuilder group)
        {
            group.CreateCommand("playlist")
                .Description("I'll give you the list of songs in the playlist.")
                .FlagMusic(true)
                .Do(e => e.Channel.SendMessage(playlist[e.User.VoiceChannel.Id].SongList()));

            group.CreateCommand("songcount")
                .Alias("playlist size")
                .FlagMusic(true)
                .Do(e => e.Channel.SendMessage(playlist[e.User.VoiceChannel.Id].SongCount()));

            group.CreateCommand("song")
                .Description("I'll tell you the song I'm currently playing.")
                .FlagMusic(true)
                .Do(e => e.Channel.SendMessage(playlist[e.User.VoiceChannel.Id].CurrentSong()));

            YT.CreateCommand(group, "ytrequest");

            if (Program.config["SoundCloud"].HasValues)
            {
                SC sc = new SC(Program.config["SoundCloud"]["client_id"].ToString(), Console.Title);
                sc.CreateSearchCmd(group, "scsearch", "scs", false);
                sc.CreateSearchCmd(group, "scsrandom", "scsr", false, SC.SearchType.Random);
                sc.CreateSearchCmd(group, "scsall", new[] {"scsmultiple", "scsa", "scsmulti"}, false, SC.SearchType.Multiple);
                sc.CreatePermalinkCmd(group, "screquest", new[]{"sctrack", "sctr"}, false);
                sc.CreatePermalinkCmd(group, "scplaylist", "scpl", true);
                sc.CreateSearchCmd(group, "scplsearch", "scpls", true);
                sc.CreateSearchCmd(group, "scplsrandom", "scplsr", true, SC.SearchType.Random);
                sc.CreateSearchCmd(group, "scplsall", new[]{"scplsmultiple", "scplsa", "scplsm"}, true, SC.SearchType.Multiple);
            }

            if (HasFolder())
            {
                Local.CreateCommand(group, "request", false/*, false*/);
                Local.CreateCommand(group, "requestall", true/*, false*/);
                //Local.CreateCommand(group, "requestpl", false, true);
                //Local.CreateCommand(group, "requestplall", true, true);
            }

            group.CreateCommand("skip")
                .Description("Vote to skip the current song. (Will skip at 50% or more)")
                .FlagMusic(true)
                .Do(e => playlist[e.User.VoiceChannel.Id].Skip(e));

            group.CreateCommand("reset")
                .Description("Vote to reset the stream. (Will reset at 50% or more)")
                .FlagMusic(true)
                .Do(e => playlist[e.User.VoiceChannel.Id].Reset(e));

            group.CreateCommand("encore")
                .Alias("replay")
                .Alias("ankoru")
                .Description("Vote to replay the current song. (Will replay at 50% or more)")
                .FlagMusic(true)
                .Do(e => playlist[e.User.VoiceChannel.Id].Encore(e));

            var gestures = Program.config["gestures"].ToString();
            if (gestures != "")
            {
                foreach (var gesture in Files(gestures))
                {
                    var file = Path.GetFileNameWithoutExtension(gesture);
                    group.CreateCommand(file)
                        .FlagMusic(true)
                        .Do(e => streams.Get(e.User.VoiceChannel).QueueGesture(gesture));
                }
                var json = Helpers.GetJsonFileIfExists($"{gestures}/gestures.json");
                if (json != null)
                {
                    foreach (var cmd_data in json)
                    {
                        var val = cmd_data.Value;
                        Func<string, string> get_real_uri = uri => YT.regex.IsMatch(uri) ? YT.VideoData.Get(uri).Uri : uri;
                        Helpers.CreateJsonCommand(group, cmd_data.Key, val, cmd =>
                        {
                            var uris = val["uris"].ToObject<string[]>();
                            if (uris.Length == 1) cmd.Do(e => streams.Get(e.User.VoiceChannel).QueueGesture(get_real_uri(uris[0])));
                            else cmd.Do(e => streams.Get(e.User.VoiceChannel).QueueGesture(get_real_uri(Helpers.Pick(uris))));
                        });
                    }
                }
            }

            // Moderator commands
            group.CreateCommand("forceskip")
                .MinPermissions(1)
                .Parameter("count", Commands.ParameterType.Optional)
                .FlagMusic(true)
                .Description("I'll skip the currently playing song(s).")
                .Do(e =>
                {
                    int count;
                    playlist[e.User.VoiceChannel.Id].SkipSongs(e.Args.Any() && int.TryParse(e.Args[0], out count) ? count : 1);
                    e.Channel.SendMessage("Forcefully skipping...");
                });

            group.CreateCommand("skiprange")
                .MinPermissions(1)
                .Parameter("index")
                .Parameter("count")
                .FlagMusic(true)
                .Description("I'll forget about `count` upcoming song(s) starting at `index`.")
                .Do(e =>
                {
                    int index, count;
                    string msg;
                    if (int.TryParse(e.Args[0], out index) && int.TryParse(e.Args[1], out count))
                    {
                        playlist[e.User.VoiceChannel.Id].SkipRange(index, count);
                        msg = "Forcefully removed songs.";
                    }
                    else msg = "Invalid input.";
                    e.Channel.SendMessage(msg);
                });

            group.CreateCommand("skiplast")
                .MinPermissions(1)
                .Parameter("count", Commands.ParameterType.Optional)
                .FlagMusic(true)
                .Description("I'll forget about the last song(s) currently in the playlist.")
                .Do(e =>
                {
                    int count;
                    playlist[e.User.VoiceChannel.Id].SkipLastSongs(e.Args.Any() && int.TryParse(e.Args[0], out count) ? count : 1);
                    e.Channel.SendMessage("Forcefully removed songs.");
                });

            group.CreateCommand("forcereset")
                .MinPermissions(1)
                .FlagMusic(true)
                .Description("I'll reset the stream in case of bugs, while keeping the playlist intact.")
                .Do(async e =>
                {
                    await e.Channel.SendMessage("Reseting stream...");
                    await streams.Reset(e.User.VoiceChannel);
                });

            group.CreateCommand("pause")
                .Alias("unpause")
                .MinPermissions(1)
                .FlagMusic(true)
                .Description("I'll toggle pause on the stream")
                .Do(e => playlist[e.User.VoiceChannel.Id].Pause(e));

            group.CreateCommand("repeat")
                .MinPermissions(1)
                .FlagMusic(true)
                .Description("I'll toggle repeat mode on the stream")
                .Do(e => playlist[e.User.VoiceChannel.Id].Repeat(e));

            // Administrator commands
            group.CreateCommand("music")
                .Parameter("on/off", Commands.ParameterType.Required)
                .Description("I'll start or end a stream in a particular voice channel, which you need to be in. (Turning this on will allow you to play gestures as well.)")
                .MinPermissions(2)
                .Do(e =>
                {
                    if (e.User.VoiceChannel == null) e.Channel.SendMessage($"{e.User.Mention}, you need to be in a voice channel to use this.");
                    else Helpers.OnOffCmd(e, async on =>
                    {
                        var stream = streams.Get(e.User.VoiceChannel);
                        string status = on ? "start" : "halt";
                        if ((stream != null) == on)
                        {
                            if (on && stream.Request) // The user is switching back to normal streaming mode.
                            {
                                await e.Channel.SendMessage("Switching to normal streaming mode.");
                                await streams.Play(e, false, stream);
                            }
                            else
                            {
                                string blah = on ? "streaming in! Did you mean to !reset or !forcereset the stream?" : "not streaming in!";
                                await e.Channel.SendMessage($"{e.User.Mention}, I can't {status} streaming in a channel that I'm already {blah}");
                            }
                        }
                        else
                        {
                            await e.Channel.SendMessage($"{e.User.Mention}, I'm {status}ing the stream!");
                            if (on) await streams.Play(e, false);
                            else streams.Get(e.User.VoiceChannel).Stop();
                        }
                    });
                });

            if (HasFolder()) // Request-driven mode is always on when we don't have a folder, therefore we won't need this command.
            {
                group.CreateCommand("music request")
                    .Alias("gesture mode activate")
                    .Description("I'll turn request-driven streaming on in a particular voice channel, which you need to be in. (This will allow you to play gestures)")
                    .MinPermissions(2)
                    .Do(e =>
                    {
                        var stream = streams.Get(e.User.VoiceChannel);
                        if (stream != null && stream.Request)
                        {
                            e.Channel.SendMessage("The stream is already in request mode.");
                            return;
                        }
                        Task.Run(() => streams.Play(e, true, stream));
                        e.Channel.SendMessage("I am now streaming in request-driven mode.");
                    });
            }
        }
    }
}
