You'll need to make a config.json file in the same place as the executable using this template:

```javascript
{
    "email" : "myemail@example.com",
    "password" : "MySuperSecretPassword123",
    "SoundCloud" : {},
    "LastFM" : {},
    "Booru" : {},
    "prefix" : "!",
    "master" : 63296013791666176,
    "server" : "https://discord.gg/0Lv5NLFEoz3P07Aq",
    "helpmode" : "private",
    "prefixprivate" : true,
    "prefixpublic" : true,
    "mentioncommand" : 1,
    "loglevel" : 2,
    "game" : "Happy Nekobot Fun Times",
    "musicFolder" : "",
    "musicUseSubfolders" : false,
    "pitur" : "",
    "gold" : "",
    "cosplay" : ""
}
```

The email and password lines are self explanatory.

The SoundCloud line holds credentials for SoundCloud, enabling the soundcloud commands. [You'll need to register the app](https://soundcloud.com/you/apps/). At the moment all that's needed is the Client ID `"client_id" : "Put Client ID here"`

The LastFM line holds credentials for LastFM. [Create an API account](http://www.last.fm/api/account/create). You'll need to add in `"apikey" : "Your API key", "apisecret" : "Your Shared secret"`.

The Booru map holds submaps of information(credentials and default tags) for the boorus we connect to, this allows you to increase operability depending on the booru.
If you specify a `"default_tags" : []`, you'll be able to require or blacklist certain tags in booru queries.
Some apis need credentials to work at all, or to work better. The names of the subarrays can be found in Image.cs inside the Image.Board.Get function, the properties needed in these arrays are `"login"` and either `"api_key"` or `"password_hash"`, the booru's site should tell you what should go here.

The prefix is a list of characters that can be the character used in front of literally every command.

As for the master, it's value should be the 17-digits id of the Discord account that should be recognized as the master. (The master id gets a level 10 permission level, although no official commands are above level 3.)

The server line allows you to make the bot join a server on startup using that. The value can either be the full invite link as shown above, or only the characters at the end (ex.: the "0Lv5NLFEoz3P07Aq" part of "https://discord.gg/0Lv5NLFEoz3P07Aq".)

If the server line is empty, the bot will not join any channel, and to make it be of use, you'll have to manually connect into the bot's account and join a channel.

The helpmode setting has three settings "public", "private", and disabled, if it's disabled, there'll be no help. If it's public, the help command will be responded to in the channel it's issued. If it's private, responses will be in PM.

The prefixprivate setting requires the use of prefix in PMs when true.

The prefixpublic setting requires the use of prefix in channels when true.

The mentioncommand setting allows @mentioning the bot instead of using prefix in channels when 1, when 2 allows you to mention after the command and its args as well.

The loglevel setting is how verbose your console output should be, between 1 and 5, 5 being the noisiest, you probably don't want that.

The game setting is the game Nekobot will be shown as playing by default. (Empty string will be no game).

The musicFolder setting should be set to the full path to a folder containing a bunch of music files to be used for music streaming.

The musicUseSubfolders setting is whether or not to include files buried in folders within your musicFolder.

The pitur, gold and cosplay settings should be set to full paths to image folders containing whatever images you please. If they're not set, the commands will be disabled.
