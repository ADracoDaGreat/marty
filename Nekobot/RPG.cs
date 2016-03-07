﻿using System;
using RollGen.Domain;

namespace Nekobot
{
    static class RPG
    {
        internal static void AddCommands(Commands.CommandGroupBuilder group)
        {
            group.CreateCommand("rand")
                .Parameter("min", Commands.ParameterType.Optional)
                .Parameter("max", Commands.ParameterType.Optional)
                .Description("I'll give you a random number between *min* and *max*. Both are optional. If only one is given, it's *max*. (defaults: 1-100)")
                .Do(async e =>
                {
                    foreach (string s in e.Args)
                    {
                        int dummy = 0;
                        if (!int.TryParse(s, out dummy))
                        {
                            await e.Channel.SendMessage($"{s} is not a number!");
                            return;
                        }
                    }
                    int min = e.Args.Length > 1 ? int.Parse(e.Args[0]) : 1;
                    int max = e.Args.Length > 0 ? int.Parse(e.Args[e.Args.Length == 1 ? 0 : 1]) : 100;
                    if (min == max)
                    {
                        await e.Channel.SendMessage($"You're joking right? It's {min}.");
                        return;
                    }
                    if (min > max)
                    {
                        int z = min;
                        min = max;
                        max = z;
                    }
                    await e.Channel.SendMessage($"Your number is **{new Random().Next(min,max+1)}**.");
                });

            var dd = new DomainDice(new AlbatrossExpressionEvaluator(Albatross.Expression.Parser.GetParser()), new RandomPartialRollFactory(new Random()));
            group.CreateCommand("roll")
                .Parameter("[times]t [dice expressions]", Commands.ParameterType.Unparsed)
                .Description("I'll roll a dice expression([count]d[sides]k[kept][mods...]...) as many `times` as you ask(default 1). (If empty or just `times`, will roll default: 1d6.)")
                .Do(async e =>
                {
                    var chan = e.Channel;
                    if (e.Args[0].ToLower() == "rick")
                    {
                        await chan.SendMessage("https://youtu.be/dQw4w9WgXcQ");
                        return;
                    }
                    var args = string.Join(" ", e.Args);
                    int times;
                    if ((times = args.IndexOf("t")) != -1)
                    {
                        int t = times;
                        if (int.TryParse(args.Substring(0, t), out times))
                        {
                            if (times <= 0)
                            {
                                await chan.SendMessage($"0, baka!");
                                return;
                            }
                            args = args.Substring(t+1);
                        }
                        else times = 1;
                    }
                    else times = 1;

                    string response = "";
                    double? total = times > 1 ? (int?)0 : null;
                    bool do_default = args == ""; // Default roll.
                    for (; times != 0; --times)
                    {
                        double val;
                        if (do_default)
                        {
                            val = dd.Roll().d6();
                            response += $"{val} {(total == null ? "" : times == 1 ? "=" : "+")} ";
                        }
                        else
                        {
                            try
                            {
                                var roll = dd.ReplaceRollsWithSum(args);
                                var eval = dd.Evaluate(roll);
                                val = dd.ChangeType<double>(eval);
                                if (response != string.Empty) response += '\n';
                                var str = dd.BooleanOrType<double>(eval);
                                if (roll != str) response += $"{Discord.Format.Code(roll)} = ";
                                response += $"**{str}**.";
                            }
                            catch
                            {
                                await chan.SendMessage("Incorrect Argument Syntax!");
                                return;
                            }
                        }
                        if (total != null) total += val;
                    }
                    if (total != null)
                    {
                        if (!do_default) response += "\nTotal Result = ";
                        response += $"**{total}**.";
                    }
                    await chan.SendMessage(response);
                });

            group.CreateCommand("rollsentence")
                .Parameter("[sentence]", Commands.ParameterType.Unparsed)
                .Description("I'll replace all instances of dice expressions wrapped like {1d4} with their resolutions. (see ` help roll` for info on dice expressions)")
                .Do(async e => await e.Channel.SendMessage(e.Args[0] == "" ? "" : dd.ReplaceWrappedExpressions<double>(e.Args[0])));
        }
    }
}
