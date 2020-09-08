using System;
using System.IO;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.Webhook;
using Discord.WebSocket;
using Eco.Core.Plugins.Interfaces;
using Eco.Core.Utils;
using Eco.Gameplay.GameActions;
using Eco.Gameplay.Players;
using Eco.Gameplay.Systems.Chat;
using Eco.Shared.Localization;
using Eco.Shared.Services;
using Eco.Shared.Utils;
using Microsoft.Extensions.DependencyInjection;
using Crossing.Services;
using System.Collections.Generic;
using Newtonsoft.Json;
using Eco.Gameplay.Systems.Tooltip;
using Eco.Shared.Items;
using Eco.Gameplay.Economy;
using Eco.Gameplay.Economy.Contracts;
using System.Linq;
using System.Runtime.CompilerServices;
using Eco.World;
using Eco.Gameplay.Economy.WorkParties;

namespace Crossing
{
    public class ChatRelay : IGameActionAware
    {
        private static ContractManager ContractManager => Singleton<ContractManager>.Obj;
        private static WorkPartyManager WorkPartyManager => AutoSingleton<WorkPartyManager>.Obj;
        private static Guild Guild => AutoSingleton<Guild>.Obj;
        private readonly IdentityManager _identity;
        private readonly Blathers _blathers;
        private readonly DiscordWebhookClient _generalWebhook;
        private readonly DiscordWebhookClient _activityWebhook;
        private readonly DiscordWebhookClient _govWebhook;
        private readonly DiscordWebhookClient _workWebhook;

        public ChatRelay(IServiceProvider services)
        {
            UserManager.OnUserLoggedIn.Add(OnUserLogin);
            UserManager.OnUserLoggedOut.Add(OnUserLogout);
            _identity = services.GetRequiredService<IdentityManager>();
            _blathers = services.GetRequiredService<Blathers>();
            _generalWebhook = new DiscordWebhookClient(
                $"https://discordapp.com/api/webhooks/751573692974366762/{Environment.GetEnvironmentVariable("GENERAL_TOKEN")}"
            );
            _activityWebhook = new DiscordWebhookClient(
                $"https://discordapp.com/api/webhooks/752403339513167913/{Environment.GetEnvironmentVariable("ACTIVITY_TOKEN")}"
            );
            _govWebhook = new DiscordWebhookClient(
                $"https://discordapp.com/api/webhooks/752408226821308508/{Environment.GetEnvironmentVariable("GOV_TOKEN")}"
            );
            _workWebhook = new DiscordWebhookClient(
                $"https://discordapp.com/api/webhooks/752574266272383037/{Environment.GetEnvironmentVariable("WORK_TOKEN")}"
            );
        }

        private void OnUserLogin(User user)
        {
            string username = Username(user);
            ECOMessage(_activityWebhook, $"**{username}** has logged in.").Wait();
        }

        private void OnUserLogout(User user)
        {
            string username = Username(user);
            ECOMessage(_activityWebhook, $"**{username}** has logged out.").Wait();
        }

        public void ActionPerformed(GameAction action)
        {
            ActionAsync(action).Wait();
        }

        private async Task ActionAsync(GameAction action)
        {
            switch (action)
            {
                case ChatSent chat:
                    if (chat.Tag != DefaultChatTags.General.TagName()) { break; }

                    SocketUser discordUser = DiscordUser(chat.Citizen);
                    if (discordUser == null) { break; }

                    Log.WriteLine(Localizer.Do($"ECO->Discord {discordUser.Username}: {chat.Message}"));
                    await _generalWebhook.SendMessageAsync(
                        chat.Message,
                        false,
                        null,
                        discordUser.Username,
                        Guild.EcoGlobeAvatar
                    );
                    break;
                case StartElection election:
                    if (election.ElectedTitle == null) { break; }

                    string username = Username(election.Citizen);
                    string timeLeft = TimeFormatter.FormatSimple(TimeSpan.FromSeconds(election.ElectionProcess.Election.TimeLeft));
                    await ECOMessage(_govWebhook, $"**{username}** started an election for **{election.ElectedTitle}**! The election will end in **{timeLeft}**.");
                    break;
                case JoinOrLeaveElection election:
                    username = Username(election.Citizen);
                    switch (election.EnteredOrLeftElection)
                    {
                        case EnteredOrLeftElection.EnteringElection:
                            var electionEmbed = new EmbedBuilder
                            {
                                Author = AuthorBuilder(election.Citizen),
                                Description = election.Election.GetChoiceById(election.Citizen.Id).Describe
                            };

                            await ECOEmbed(
                                _govWebhook,
                                $"**{username}** has entered the election for **{election.Election.PositionForWinner}**!",
                                electionEmbed.Build()
                            );
                            break;
                        case EnteredOrLeftElection.LeavingElection:
                            await ECOMessage(_govWebhook, $"**{username}** has left the election for **{election.Election.PositionForWinner}**.");
                            break;
                    }
                    break;
                case WonElection election:
                    username = Username(election.Citizen);
                    await ECOMessage(_govWebhook, $"**{username}** has won the election for **{election.Election.PositionForWinner}**!");
                    break;
                case DemographicChange change:
                    username = Username(change.Citizen);
                    switch (change.Entered)
                    {
                        case EnteredOrLeftDemographic.EnteringDemographic:
                            var changeEmbed = new EmbedBuilder
                            {
                                Description = change.Demographic.Description()
                            };
                            await ECOEmbed(
                                _govWebhook,
                                $"**{username}** became a part of **{change.Demographic.Name}**!",
                                changeEmbed.Build()
                            );
                            break;
                        case EnteredOrLeftDemographic.LeavingDemographic:
                            await ECOMessage(_govWebhook, $"**{username}** is no longer a part of **{change.Demographic.Name}.**");
                            break;
                    }
                    break;
                case PropertyTransfer propTransfer:
                    string executor = Username(propTransfer.Citizen);
                    string currentOwner = Username(propTransfer.CurrentOwner.Name);
                    string newOwner = Username(propTransfer.NewOwner.Name);

                    await ECOMessage(_activityWebhook, $"**{executor}** transferred a property of **{currentOwner}** to **{newOwner}**.");
                    break;
                case ClaimOrUnclaimProperty propClaim:
                    username = Username(propClaim.Citizen);
                    switch (propClaim.ClaimedOrUnclaimed)
                    {
                        case ClaimedOrUnclaimed.ClaimingLand:
                            await ECOMessage(_activityWebhook, $"**{username}** claimed land at **{propClaim.Location.ToString()}**");
                            break;
                        case ClaimedOrUnclaimed.UnclaimingLand:
                            await ECOMessage(_activityWebhook, $"**{username}** unclaimed land at **{propClaim.Location.ToString()}**");
                            break;
                    }
                    break;
                case ReceiveGovernmentFunds govFunds:
                    username = Username(govFunds.Citizen);
                    await ECOMessage(_activityWebhook, $"**{username}** has received **{govFunds.Amount} {govFunds.Currency.Name}** for government work.");
                    break;
                case PostedContract postedContract:
                    IEnumerable<Contract> contracts = from x in Enumerable.Where(ContractManager.Contracts, (Contract x) => x.Client == postedContract.Client.Name)
                        orderby x.CreationTime
                        select x;

                    Contract contract = contracts.Last();

                    var contractEmbed = new EmbedBuilder
                    {
                        Author = AuthorBuilder(postedContract.Client),
                        Description = contract.ClauseDesc()
                    };

                    username = Username(postedContract.Client);
                    await ECOEmbed(
                        _workWebhook,
                        $"**{username}** has posted a contract for **{postedContract.CurrencyAmount} {postedContract.Currency.Name}**!",
                        contractEmbed.Build()
                    );
                    break;
                case PostedWorkParty postedWorkParty:
                    IEnumerable<WorkParty> relevantParties = WorkPartyManager.RelevantWorkParties(postedWorkParty.Client.Player);
                    IEnumerable<WorkParty> workParties = from x in Enumerable.Where(relevantParties, (WorkParty x) => x.Creator == postedWorkParty.Client)
                        orderby x.CreationTime
                        select x;

                    WorkParty workParty = workParties.Last();

                    var workPartyEmbed = new EmbedBuilder
                    {
                        Author = AuthorBuilder(postedWorkParty.Client),
                        Description = workParty.Description()
                    };

                    username = Username(postedWorkParty.Client);
                    await ECOEmbed(
                        _workWebhook,
                        $"**{username}** has posted a contract for **{postedWorkParty.CurrencyAmount} {postedWorkParty.Currency.Name}**!",
                        workPartyEmbed.Build()
                    );
                    break;
                case GainSpecialty gainSpecialty:
                    username = Username(gainSpecialty.Citizen);
                    await ECOMessage(_activityWebhook, $"**{username}** took a new specialty in **{gainSpecialty.Specialty.DisplayName}**");
                    break;
                case GainProfession gainProfession:
                    break;
                case CreateWorkOrder createWork:
                    username = Username(createWork.Citizen);
                    await ECOMessage(_activityWebhook, $"**{username}** started a work order for **{createWork.WorkOrder.DisplayName}**");
                    break;
                case AddToWorkOrderAction addWork:
                    username = Username(addWork.Citizen);
                    await ECOMessage(_activityWebhook, $"**{username}** contributed **{addWork.ItemsMoved}** **{addWork.ItemUsed.DisplayName}** to the work order **{addWork.WorkOrder.DisplayName}**");
                    break;
                case LaborWorkOrderAction laborWork:
                    username = Username(laborWork.Citizen);
                    await ECOMessage(_activityWebhook, $"**{username}** performed **{laborWork.LaborAdded}** units of labor on **{laborWork.WorkOrder.DisplayName}**");
                    break;
                default:
                    break;
            }
        }

        public Result ShouldOverrideAuth(GameAction action)
        {
            return Result.FailedNoMessage;
        }

        private async Task ECOMessage(DiscordWebhookClient webhookClient, string msg)
        {
            await webhookClient.SendMessageAsync(
                msg,
                false,
                null,
                "ECO",
                Guild.EcoGlobeAvatar
            );
        }

        private async Task ECOEmbed(DiscordWebhookClient webhookClient, string msg, Embed embed)
        {
            List<Embed> embeds = new List<Embed>();
            embeds.Add(embed);

            await webhookClient.SendMessageAsync(
                msg,
                false,
                embeds,
                "ECO",
                Guild.EcoGlobeAvatar
            );
        }

        private EmbedAuthorBuilder AuthorBuilder(User user)
        {
            SocketUser discordUser = DiscordUser(user);
            var author = new EmbedAuthorBuilder
            {
                Name = Username(user)
            };
            if (discordUser != null)
            {
                author.WithIconUrl(discordUser.GetDefaultAvatarUrl());
            }
            return author;
        }

        private SocketUser DiscordUser(User user)
        {
            string discordId = _identity.SteamToDiscord.GetOrDefault(user.SteamId);
            if (discordId == "") { return null; }

            ulong id = Convert.ToUInt64(discordId);
            return _blathers.SocketGuild().GetUser(id);
        }

        private string Username(User user)
        {
            SocketUser discordUser = DiscordUser(user);
            if (discordUser == null)
            {
                return user.Name;
            }
            return discordUser.Username;
        }

        private string Username(string name)
        {
            User user = UserManager.FindUserByName(name);
            if (user == null)
            {
                return "";
            }
            return Username(user);
        }
    }
}