using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using App.Metrics;
using Discord;
using Discord.WebSocket;
using NodaTime.Extensions;
using PluralKit.Core;
using Serilog;

namespace PluralKit.Bot
{
    public class PeriodicStatCollector
    {
        private DiscordShardedClient _client;
        private IMetrics _metrics;

        private SystemStore _systems;
        private MemberStore _members;
        private SwitchStore _switches;
        private MessageStore _messages;

        private ILogger _logger;

        public PeriodicStatCollector(IDiscordClient client, IMetrics metrics, SystemStore systems, MemberStore members, SwitchStore switches, MessageStore messages, ILogger logger)
        {
            _client = (DiscordShardedClient) client;
            _metrics = metrics;
            _systems = systems;
            _members = members;
            _switches = switches;
            _messages = messages;
            _logger = logger.ForContext<PeriodicStatCollector>();
        }

        public async Task CollectStats()
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            
            // Aggregate guild/channel stats
            _metrics.Measure.Gauge.SetValue(BotMetrics.Guilds, _client.Guilds.Count);
            _metrics.Measure.Gauge.SetValue(BotMetrics.Channels, _client.Guilds.Sum(g => g.TextChannels.Count));

            // Aggregate member stats
            var usersKnown = new HashSet<ulong>();
            var usersOnline = new HashSet<ulong>();
            foreach (var guild in _client.Guilds)
            foreach (var user in guild.Users)
            {
                usersKnown.Add(user.Id);
                if (user.Status == UserStatus.Online) usersOnline.Add(user.Id);
            }

            _metrics.Measure.Gauge.SetValue(BotMetrics.MembersTotal, usersKnown.Count);
            _metrics.Measure.Gauge.SetValue(BotMetrics.MembersOnline, usersOnline.Count);
            
            // Aggregate DB stats
            _metrics.Measure.Gauge.SetValue(CoreMetrics.SystemCount, await _systems.Count());
            _metrics.Measure.Gauge.SetValue(CoreMetrics.MemberCount, await _members.Count());
            _metrics.Measure.Gauge.SetValue(CoreMetrics.SwitchCount, await _switches.Count());
            _metrics.Measure.Gauge.SetValue(CoreMetrics.MessageCount, await _messages.Count());

            stopwatch.Stop();
            _logger.Information("Updated metrics in {Time}", stopwatch.ElapsedDuration());
        }
    }
}