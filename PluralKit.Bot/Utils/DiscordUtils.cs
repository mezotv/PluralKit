using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.Exceptions;

using NodaTime;

using PluralKit.Core;

namespace PluralKit.Bot
{
    public static class DiscordUtils
    {
        public static DiscordColor Blue = new DiscordColor(0x1f99d8);
        public static DiscordColor Green = new DiscordColor(0x00cc78);
        public static DiscordColor Red = new DiscordColor(0xef4b3d);
        public static DiscordColor Gray = new DiscordColor(0x979c9f);
        
        public static Permissions DM_PERMISSIONS = (Permissions) 0b00000_1000110_1011100110000_000000;
        
        private static readonly Regex USER_MENTION = new Regex("<@!?(\\d{17,19})>");
        private static readonly Regex ROLE_MENTION = new Regex("<@&(\\d{17,19})>");
        private static readonly Regex EVERYONE_HERE_MENTION = new Regex("@(everyone|here)");

        private static readonly FieldInfo _roleIdsField = typeof(DiscordMember).GetField("_role_ids", BindingFlags.NonPublic | BindingFlags.Instance);

        public static string NameAndMention(this DiscordUser user) {
            return $"{user.Username}#{user.Discriminator} ({user.Mention})";
        }

        // We funnel all "permissions from DiscordMember" calls through here 
        // This way we can ensure we do the read permission correction everywhere
        private static Permissions PermissionsInGuild(DiscordChannel channel, DiscordMember member)
        {
            ValidateCachedRoles(member);
            var permissions = channel.PermissionsFor(member);
            
            // This method doesn't account for channels without read permissions
            // If we don't have read permissions in the channel, we don't have *any* permissions
            if ((permissions & Permissions.AccessChannels) != Permissions.AccessChannels)
                return Permissions.None;
            
            return permissions;
        }

        // Workaround for DSP internal error
        private static void ValidateCachedRoles(DiscordMember member)
        {
            var roleIdCache = _roleIdsField.GetValue(member) as List<ulong>;
            var currentRoleIds = member.Roles.Where(x => x != null).Select(x => x.Id);
            var invalidRoleIds = roleIdCache.Where(x => !currentRoleIds.Contains(x));
            roleIdCache.RemoveAll(x => invalidRoleIds.Contains(x));
        }

        public static async Task<Permissions> PermissionsIn(this DiscordChannel channel, DiscordUser user)
        {
            // Just delegates to PermissionsInSync, but handles the case of a non-member User in a guild properly
            // This is a separate method because it requires an async call
            if (channel.Guild != null && !(user is DiscordMember))
            {
                var member = await channel.Guild.GetMember(user.Id);
                if (member != null)
                    return PermissionsInSync(channel, member);
            }
            
            return PermissionsInSync(channel, user);
        }
        
        // Same as PermissionsIn, but always synchronous. DiscordUser must be a DiscordMember if channel is in guild.
        public static Permissions PermissionsInSync(this DiscordChannel channel, DiscordUser user)
        {
            if (channel.Guild != null && !(user is DiscordMember))
                throw new ArgumentException("Function was passed a guild channel but a non-member DiscordUser");
            
            if (user is DiscordMember m) return PermissionsInGuild(channel, m);
            if (channel.Type == ChannelType.Private) return DM_PERMISSIONS;
            return Permissions.None;
        }

        public static Permissions BotPermissions(this DiscordChannel channel)
        {
            // TODO: can we get a CurrentMember somehow without a guild context?
            // at least, without somehow getting a DiscordClient reference as an arg(which I don't want to do)
            if (channel.Guild != null)
                return PermissionsInSync(channel, channel.Guild.CurrentMember);
            if (channel.Type == ChannelType.Private) return DM_PERMISSIONS;
            return Permissions.None;
        }

        public static bool BotHasAllPermissions(this DiscordChannel channel, Permissions permissionSet) =>
            (BotPermissions(channel) & permissionSet) == permissionSet;

        public static Instant SnowflakeToInstant(ulong snowflake) => 
            Instant.FromUtc(2015, 1, 1, 0, 0, 0) + Duration.FromMilliseconds(snowflake >> 22);

        public static ulong InstantToSnowflake(Instant time) =>
            (ulong) (time - Instant.FromUtc(2015, 1, 1, 0, 0, 0)).TotalMilliseconds >> 22;

        public static ulong InstantToSnowflake(DateTimeOffset time) =>
            (ulong) (time - new DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalMilliseconds >> 22;

        public static async Task CreateReactionsBulk(this DiscordMessage msg, string[] reactions)
        {
            foreach (var reaction in reactions)
            {
                await msg.CreateReactionAsync(DiscordEmoji.FromUnicode(reaction));
            }
        }

        public static string WorkaroundForUrlBug(string input)
        {
            // Workaround for https://github.com/DSharpPlus/DSharpPlus/issues/565
            return input?.Replace("%20", "+");
        }
        
        public static Task<DiscordMessage> SendMessageFixedAsync(this DiscordChannel channel, string content = null, DiscordEmbed embed = null, IEnumerable<IMention> mentions = null) =>
            // Passing an empty list blocks all mentions by default (null allows all through)
            channel.SendMessageAsync(content, embed: embed, mentions: mentions ?? new IMention[0]);
        
        // This doesn't do anything by itself (DiscordMember.SendMessageAsync doesn't take a mentions argument)
        // It's just here for consistency so we don't use the standard SendMessageAsync method >.>
        public static Task<DiscordMessage> SendMessageFixedAsync(this DiscordMember member, string content = null, DiscordEmbed embed = null) =>
            member.SendMessageAsync(content, embed: embed);

        public static bool TryGetCachedUser(this DiscordClient client, ulong id, out DiscordUser user)
        {
            user = null;
            
            var cache = (ConcurrentDictionary<ulong, DiscordUser>) typeof(BaseDiscordClient)
                .GetProperty("UserCache", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(client);
            return cache != null && cache.TryGetValue(id, out user);
        }
        
        public static DiscordColor? ToDiscordColor(this string color)
        {
            if (int.TryParse(color, NumberStyles.HexNumber, null, out var colorInt))
                return new DiscordColor(colorInt);
            throw new ArgumentException($"Invalid color string '{color}'.");
        }
        
        public static bool HasMentionPrefix(string content, ref int argPos, out ulong mentionId)
        {
            mentionId = 0;
            
            // Roughly ported from Discord.Commands.MessageExtensions.HasMentionPrefix
            if (string.IsNullOrEmpty(content) || content.Length <= 3 || (content[0] != '<' || content[1] != '@'))
                return false;
            int num = content.IndexOf('>');
            if (num == -1 || content.Length < num + 2 || content[num + 1] != ' ' || !TryParseMention(content.Substring(0, num + 1), out mentionId))
                return false;
            argPos = num + 2;
            return true;
        }

        public static bool TryParseMention(this string potentialMention, out ulong id)
        {
            if (ulong.TryParse(potentialMention, out id)) return true;

            var match = USER_MENTION.Match(potentialMention);
            if (match.Success && match.Index == 0 && match.Length == potentialMention.Length) 
            {
                id = ulong.Parse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
                return true;
            }

            return false;
        }

        public static IEnumerable<IMention> ParseAllMentions(this string input, bool allowEveryone = false)
        {
            var mentions = new List<IMention>();
            mentions.AddRange(USER_MENTION.Matches(input)
                .Select(x => new UserMention(ulong.Parse(x.Groups[1].Value)) as IMention));
            mentions.AddRange(ROLE_MENTION.Matches(input)
                .Select(x => new RoleMention(ulong.Parse(x.Groups[1].Value)) as IMention));
            if (EVERYONE_HERE_MENTION.IsMatch(input) && allowEveryone)
                mentions.Add(new EveryoneMention());
            return mentions;
        }

        public static string EscapeMarkdown(this string input)
        {
            Regex pattern = new Regex(@"[*_~>`(||)\\]", RegexOptions.Multiline);
            if (input != null) return pattern.Replace(input, @"\$&");
            else return input;
        }

        public static string EscapeBacktickPair(this string input){
            Regex doubleBacktick = new Regex(@"``", RegexOptions.Multiline);
            //Run twice to catch any pairs that are created from the first pass, pairs shouldn't be created in the second as they are created from odd numbers of backticks, even numbers are all caught on the first pass
            if(input != null) return doubleBacktick.Replace(doubleBacktick.Replace(input, @"`‌﻿`"),@"`‌﻿`");
            else return input;
        }

        public static Task<DiscordUser> GetUser(this DiscordRestClient client, ulong id) => 
            WrapDiscordCall(client.GetUserAsync(id));

        public static Task<DiscordUser> GetUser(this DiscordClient client, ulong id) => 
            WrapDiscordCall(client.GetUserAsync(id));

        public static Task<DiscordChannel> GetChannel(this DiscordRestClient client, ulong id) => 
            WrapDiscordCall(client.GetChannelAsync(id));
        
        public static Task<DiscordChannel> GetChannel(this DiscordClient client, ulong id) => 
            WrapDiscordCall(client.GetChannelAsync(id));
        
        public static Task<DiscordGuild> GetGuild(this DiscordRestClient client, ulong id) => 
            WrapDiscordCall(client.GetGuildAsync(id));
        
        public static Task<DiscordGuild> GetGuild(this DiscordClient client, ulong id) => 
            WrapDiscordCall(client.GetGuildAsync(id));
        
        public static Task<DiscordMember> GetMember(this DiscordRestClient client, ulong guild, ulong user)
        {
            async Task<DiscordMember> Inner() => 
                await (await client.GetGuildAsync(guild)).GetMemberAsync(user);
            return WrapDiscordCall(Inner());
        }
        public static Task<DiscordMember> GetMember(this DiscordClient client, ulong guild, ulong user)
        {
            async Task<DiscordMember> Inner() => 
                await (await client.GetGuildAsync(guild)).GetMemberAsync(user);
            return WrapDiscordCall(Inner());
        }

        public static Task<DiscordMember> GetMember(this DiscordGuild guild, ulong user) => 
            WrapDiscordCall(guild.GetMemberAsync(user));

        public static Task<DiscordMessage> GetMessage(this DiscordChannel channel, ulong id) =>
            WrapDiscordCall(channel.GetMessageAsync(id));

        public static Task<DiscordMessage> GetMessage(this DiscordRestClient client, ulong channel, ulong message) =>
            WrapDiscordCall(client.GetMessageAsync(channel, message));

        private static async Task<T> WrapDiscordCall<T>(Task<T> t)
            where T: class
        {
            try
            {
                return await t;
            }
            catch (NotFoundException)
            {
                return null;
            }
            catch (UnauthorizedException)
            {
                return null;
            }
        }

        public static DiscordEmbedBuilder WithSimpleLineContent(this DiscordEmbedBuilder eb, IEnumerable<string> lines)
        {
            static int CharacterLimit(int pageNumber) =>
                // First chunk goes in description (2048 chars), rest go in embed values (1000 chars)
                pageNumber == 0 ? 2048 : 1000;

            var linesWithEnding = lines.Select(l => $"{l}\n");
            var pages = StringUtils.JoinPages(linesWithEnding, CharacterLimit);

            // Add the first page to the embed description
            if (pages.Count > 0)
                eb.WithDescription(pages[0]);
            
            // Add the rest to blank-named (\u200B) fields
            for (var i = 1; i < pages.Count; i++)
                eb.AddField("\u200B", pages[i]);

            return eb;
        }
    }
}