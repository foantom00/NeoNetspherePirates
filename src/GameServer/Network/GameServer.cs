﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using AuthServer.ServiceModel;
using BlubLib.DotNetty.Handlers.MessageHandling;
using BlubLib.Threading;
using ExpressMapper;
using ExpressMapper.Extensions;
using NeoNetsphere.Commands;
using NeoNetsphere.Network.Data.Chat;
using NeoNetsphere.Network.Data.Club;
using NeoNetsphere.Network.Data.Game;
using NeoNetsphere.Network.Data.GameRule;
using NeoNetsphere.Network.Message.Club;
using NeoNetsphere.Network.Message.Game;
using NeoNetsphere.Network.Message.GameRule;
using NeoNetsphere.Network.Message.Relay;
using NeoNetsphere.Network.Services;
using NeoNetsphere.Resource;
using Netsphere;
using ProudNetSrc;
using ProudNetSrc.Serialization;
using Serilog;
using Constants = Serilog.Core.Constants;

namespace NeoNetsphere.Network
{
    internal class GameServer : ProudServer
    {
        // ReSharper disable once InconsistentNaming
        private static readonly ILogger Logger = Log.ForContext(Constants.SourceContextPropertyName, nameof(GameServer))
            ;

        private readonly ServerlistManager _serverlistManager;

        private readonly ILoop _worker;

        private TimeSpan _mailBoxCheckTimer;
        private TimeSpan _saveTimer;

        private GameServer(Configuration config)
            : base(config)
        {
            RegisterMappings();

            //Logger.Information("To get sure that u know how to work with this server, i've added this");
            //Logger.Information("Remove the lines located in the given file named by this log's prefix");
            //
            ////
            //Environment.Exit(-1);
            ////

            //ServerTime = TimeSpan.Zero;

            CommandManager = new CommandManager(this);
            CommandManager.Add(new ServerCommand())
                .Add(new ReloadCommand())
                .Add(new GameCommands())
                .Add(new BanCommands())
                .Add(new AdminCommands())
                .Add(new NoticeCommand())
                .Add(new InventoryCommands());

            PlayerManager = new PlayerManager();
            ResourceCache = new ResourceCache();
            ChannelManager = new ChannelManager(ResourceCache.GetChannels());
            ClubManager = new ClubManager(ResourceCache.GetClubs()); 

            _worker = new ThreadLoop(TimeSpan.FromMilliseconds(100), Worker);
            _serverlistManager = new ServerlistManager();
        }

        public static GameServer Instance { get; private set; }

        public CommandManager CommandManager { get; }
        public PlayerManager PlayerManager { get; }
        public ChannelManager ChannelManager { get; }
        public ClubManager ClubManager { get; set; }
        public ResourceCache ResourceCache { get; }

        public static void Initialize(Configuration config)
        {
            if (Instance != null)
                throw new InvalidOperationException("Server is already initialized");

#if LATESTS4
            config.Version = new Guid("{14229beb-3338-7114-ab92-9b4af78c688f}");
#else
            config.Version = new Guid("{beb92241-8333-4117-ab92-9b4af78c688f}");
#endif

#if OLDUI
            config.Version = new Guid("{beb92241-8333-4117-ab92-9b4af78c688f}");
#endif

            config.MessageFactories = new MessageFactory[]
            {
                new RelayMessageFactory(), new GameMessageFactory(), new GameRuleMessageFactory(),
                new ClubMessageFactory()
            };
            config.SessionFactory = new GameSessionFactory();

            // ReSharper disable InconsistentNaming
            bool MustBeLoggedIn(GameSession session) => session.IsLoggedIn();
            bool MustNotBeLoggedIn(GameSession session) => !session.IsLoggedIn();
            bool MustBeInChannel(GameSession session) => session.Player.Channel != null;
            bool MustBeInRoom(GameSession session) => session.Player.Room != null;
            bool MustNotBeInRoom(GameSession session) => session.Player.Room == null;
            bool MustBeRoomHost(GameSession session) => session.Player.Room.Host == session.Player;
            bool MustBeRoomMaster(GameSession session) => session.Player.Room.Master == session.Player;
            // ReSharper restore InconsistentNaming

            config.MessageHandlers = new IMessageHandler[]
            {
                new FilteredMessageHandler<GameSession>()
                    .AddHandler(new AuthService())
                    .AddHandler(new CharacterService())
                    .AddHandler(new GeneralService())
                    .AddHandler(new AdminService())
                    .AddHandler(new ChannelService())
                    .AddHandler(new ShopService())
                    .AddHandler(new InventoryService())
                    .AddHandler(new RoomService())
                    .AddHandler(new ClubService())
                    .RegisterRule<LoginRequestReqMessage>(MustNotBeLoggedIn)
                    .RegisterRule<CharacterCreateReqMessage>(MustBeLoggedIn)
                    .RegisterRule<CharacterSelectReqMessage>(MustBeLoggedIn)
                    .RegisterRule<CharacterDeleteReqMessage>(MustBeLoggedIn)
                    .RegisterRule<AdminShowWindowReqMessage>(MustBeLoggedIn)
                    .RegisterRule<AdminActionReqMessage>(MustBeLoggedIn)
                    .RegisterRule<ChannelInfoReqMessage>(MustBeLoggedIn)
                    .RegisterRule<NewShopUpdateCheckReqMessage>(MustBeLoggedIn)
                    .RegisterRule<ChannelEnterReqMessage>(MustBeLoggedIn)
                    .RegisterRule<ChannelLeaveReqMessage>(MustBeLoggedIn, MustBeInChannel)
                    //.RegisterRule<LicenseGainReqMessage>(MustBeLoggedIn, MustBeInChannel)
                    //.RegisterRule<LicenseExerciseReqMessage>(MustBeLoggedIn, MustBeInChannel)
                    .RegisterRule<ItemBuyItemReqMessage>(MustBeLoggedIn)
                    .RegisterRule<RandomShopRollingStartReqMessage>(MustBeLoggedIn)
                    //.RegisterRule<CRandomShopItemSaleReqMessage>(MustBeLoggedIn)
                    .RegisterRule<ItemUseItemReqMessage>(MustBeLoggedIn)
                    .RegisterRule<ItemRepairItemReqMessage>(MustBeLoggedIn)
                    .RegisterRule<ItemRefundItemReqMessage>(MustBeLoggedIn)
                    .RegisterRule<ItemDiscardItemReqMessage>(MustBeLoggedIn)
                    .RegisterRule<RoomQuickJoinReqMessage>(MustBeLoggedIn, MustBeInChannel, MustNotBeInRoom)
                    .RegisterRule<RoomEnterPlayerReqMessage>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom)
                    .RegisterRule<RoomMakeReqMessage>(MustBeLoggedIn, MustBeInChannel, MustNotBeInRoom)
                    .RegisterRule<RoomMakeReq2Message>(MustBeLoggedIn, MustBeInChannel, MustNotBeInRoom)
                    .RegisterRule<RoomEnterReqMessage>(MustBeLoggedIn, MustBeInChannel, MustNotBeInRoom)
                    .RegisterRule<RoomLeaveReqMessage>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom)
                    .RegisterRule<RoomTeamChangeReqMessage>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom)
                    .RegisterRule<RoomPlayModeChangeReqMessage>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom)
                    .RegisterRule<ScoreKillReqMessage>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom)
                    .RegisterRule<ScoreKillAssistReqMessage>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom)
                    .RegisterRule<ScoreOffenseReqMessage>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom)
                    .RegisterRule<ScoreOffenseAssistReqMessage>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom)
                    .RegisterRule<ScoreDefenseReqMessage>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom)
                    .RegisterRule<ScoreDefenseAssistReqMessage>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom)
                    .RegisterRule<ScoreTeamKillReqMessage>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom)
                    .RegisterRule<ScoreHealAssistReqMessage>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom)
                    .RegisterRule<ScoreSuicideReqMessage>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom)
                    .RegisterRule<ScoreReboundReqMessage>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom, MustBeRoomHost,
                        session => session.Player.RoomInfo.State != PlayerState.Lobby &&
                                   session.Player.RoomInfo.State != PlayerState.Spectating)
                    .RegisterRule<ScoreGoalReqMessage>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom, MustBeRoomHost,
                        session => session.Player.RoomInfo.State != PlayerState.Lobby &&
                                   session.Player.RoomInfo.State != PlayerState.Spectating)
                    .RegisterRule<RoomBeginRoundReqMessage>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom,
                        MustBeRoomMaster)
                    .RegisterRule<RoomReadyRoundReqMessage>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom,
                        session => session.Player.RoomInfo.State == PlayerState.Lobby)
                    .RegisterRule<RoomBeginRoundReq2Message>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom,
                        MustBeRoomMaster)
                    .RegisterRule<GameLoadingSuccessReqMessage>(MustBeLoggedIn, MustBeInChannel)
                    .RegisterRule<RoomReadyRoundReq2Message>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom,
                        session => session.Player.RoomInfo.State == PlayerState.Lobby)
                    .RegisterRule<GameEventMessageReqMessage>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom)
                    .RegisterRule<RoomItemChangeReqMessage>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom,
                        session => session.Player.RoomInfo.State == PlayerState.Lobby)
                    .RegisterRule<GameAvatarChangeReqMessage>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom,
                        session => session.Player.RoomInfo.State == PlayerState.Lobby ||
                                   session.Player.Room.GameRuleManager.GameRule.StateMachine.IsInState(
                                       GameRuleState.HalfTime))
                    .RegisterRule<RoomChangeRuleNotifyReqMessage>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom,
                        MustBeRoomMaster,
                        session =>
                            session.Player.Room.GameRuleManager.GameRule.StateMachine.IsInState(GameRuleState.Waiting))
                    .RegisterRule<RoomChangeRuleNotifyReq2Message>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom,
                        MustBeRoomMaster,
                        session =>
                            session.Player.Room.GameRuleManager.GameRule.StateMachine.IsInState(GameRuleState.Waiting))
                    .RegisterRule<ClubAddressReqMessage>(MustBeLoggedIn, MustBeInChannel)
                    //.RegisterRule<ClubInfoReqMessage>(MustBeLoggedIn, MustBeInChannel)
                    .RegisterRule<RoomLeaveReguestReqMessage>(MustBeLoggedIn, MustBeInChannel, MustBeInRoom)
            };

            Instance = new GameServer(config);
        }

        public void BroadcastNotice(string message)
        {
            Broadcast(new NoticeAdminMessageAckMessage(message));
        }

        private void Worker(TimeSpan delta)
        {
            try
            {
                ChannelManager.Update(delta);
            }
            catch (Exception e)
            {
                Logger.Error(e.ToString());
            }

            foreach (var plr in PlayerManager.Where(plr => !plr.IsLoggedIn() &&
                                                           plr.Session?.ConnectDate.Add(TimeSpan.FromMinutes(5)) <
                                                           DateTimeOffset.Now))
            {
                plr.Session?.CloseAsync();
                if (plr.Session != null)
                    OnDisconnected(plr.Session);
            }

            foreach (var channel in ChannelManager)
            {
                try
                {
                    foreach (var room in channel.RoomManager)
                    {
                        if (room?.TeamManager == null) continue;
                        foreach (var team in room.TeamManager)
                            try
                            {
                                foreach (var plr in team.Value)
                                    try
                                    {
                                        if (!plr.Value?.IsLoggedIn() ?? false) team.Value?.Leave(plr.Value);
                                    }
                                    catch (Exception)
                                    {
                                    }
                            }
                            catch (Exception)
                            {
                            }

                        foreach (var plr in room.Players)
                            try
                            {
                                if (!plr.Value?.IsLoggedIn() ?? false) room?.Leave(plr.Value);
                            }
                            catch (Exception)
                            {
                            }
                    }

                    foreach (var plr in channel.Players)
                        try
                        {
                            if (!plr.Value?.IsLoggedIn() ?? false) channel?.Leave(plr.Value);
                        }
                        catch (Exception)
                        {
                        }
                }
                catch (Exception)
                {
                    //ignore
                }
            }
            
            //foreach (var channel in ChannelManager)
            //{
            //    foreach (var room in channel.RoomManager.Where(x => !x.Master.IsLoggedIn() || !x.Master.Session.IsConnected))
            //    {
            //        if (!room.TeamManager.Players.Any())
            //            channel?.RoomManager?.Remove(room, true);
            //        else
            //            if (!room.Players.Any(x => x.Value.IsLoggedIn() || x.Value.Session.IsConnected))
            //            channel?.RoomManager?.Remove(room, true);
            //    }
            //}

            // ToDo Use another thread for this?
            _saveTimer = _saveTimer.Add(delta);
            if (_saveTimer < Config.Instance.SaveInterval) return;
            {
                _saveTimer = TimeSpan.Zero;

                var players = PlayerManager.Where(plr => plr.IsLoggedIn());
                var enumerable = players as Player[] ?? players.ToArray();
                if (!enumerable.Any()) return;
                {
                    Logger.Information("Saving playerdata...");
                    foreach (var plr in enumerable)
                        try
                        {
                            plr.Save();
                        }
                        catch (Exception ex)
                        {
                            Logger.ForAccount(plr)
                                .Error(ex, "Failed to save playerdata");
                        }
                    Logger.Information("Saving playerdata completed");
                }
            }

            //_mailBoxCheckTimer = _mailBoxCheckTimer.Add(delta);
            //if (_mailBoxCheckTimer >= TimeSpan.FromMinutes(10))
            //{
            //    _mailBoxCheckTimer = TimeSpan.Zero;
            //
            //    //foreach (var plr in PlayerManager.Where(plr => plr.IsLoggedIn()))
            //    //    plr.Mailbox.Remove(plr.Mailbox.Where(mail => mail.Expires >= DateTimeOffset.Now));
            //}
        }

        private static void RegisterMappings()
        {
            Mapper.Register<GameServer, ServerInfoDto>()
                .Member(dest => dest.ApiKey, src => Config.Instance.AuthAPI.ApiKey)
                .Member(dest => dest.Id, src => Config.Instance.Id)
                .Member(dest => dest.Name, src => $"{Config.Instance.Name}[{Program.GlobalVersion.Major}.{Program.GlobalVersion.Major / 2 + Program.GlobalVersion.Minor + Program.GlobalVersion.Build + Program.GlobalVersion.Revision}]")
                .Member(dest => dest.PlayerLimit, src => Config.Instance.PlayerLimit)
                .Member(dest => dest.PlayerOnline, src => src.Sessions.Count)
                .Member(dest => dest.EndPoint,
                    src => new IPEndPoint(IPAddress.Parse(Config.Instance.IP), Config.Instance.Listener.Port))
                .Member(dest => dest.ChatEndPoint,
                    src => new IPEndPoint(IPAddress.Parse(Config.Instance.IP), Config.Instance.ChatListener.Port));

            Mapper.Register<Player, PlayerAccountInfoDto>()
                .Function(dest => dest.IsGM, src => src.Account.SecurityLevel > SecurityLevel.User)
                .Member(dest => dest.GameTime, src => TimeSpan.Parse(src.PlayTime))
                .Member(dest => dest.TotalExp, src => src.TotalExperience)
                .Function(dest => dest.TutorialState,
                    src => (uint) (Config.Instance.Game.EnableTutorial ? src.TutorialState : 1))
                .Member(dest => dest.Nickname, src => src.Account.Nickname)
                .Member(dest => dest.TotalMatches, src => src.TotalLosses + src.TotalWins)
                .Member(dest => dest.MatchesWon, src => src.TotalWins)
                .Member(dest => dest.MatchesLost, src => src.TotalLosses);


            Mapper.Register<Channel, ChannelInfoDto>()
                .Member(dest => dest.PlayersOnline, src => src.Players.Count);

            Mapper.Register<PlayerItem, ItemDto>()
                .Member(dest => dest.Id, src => src.Id)
                .Function(dest => dest.ExpireTime,
                    src => src.ExpireDate == DateTimeOffset.MinValue ? -1 : src.ExpireDate.ToUnixTimeSeconds())
                .Function(dest => dest.Durability, src =>
                {
                    if (src.PeriodType == ItemPeriodType.Units) return (int) src.Count;
                    return src.Durability;
                })
                .Function(dest => dest.Effects, src =>
                {
                    var desteffects = new List<ItemEffectDto>();
                    src.Effects.ToList().ForEach(eff => { desteffects.Add(new ItemEffectDto {Effect = eff}); });
                    return desteffects.ToArray();
                });

            Mapper.Register<Deny, DenyDto>()
                .Member(dest => dest.AccountId, src => src.DenyId)
                .Member(dest => dest.Nickname, src => src.Nickname);

            Mapper.Register<Player, RoomPlayerDto>()
                .Member(dest => dest.ClanId, src => (uint)src.Club.Clan_ID)
                .Member(dest => dest.AccountId, src => src.Account.Id)
                .Value(dest => dest.Unk1, (byte)0x9A)
                .Member(dest => dest.Nickname, src => src.Account.Nickname)
                .Member(dest => dest.Unk2, src => (byte)src.Room.Players.Values.ToList().IndexOf(src))
                .Member(dest => dest.IsGM, src => src.Account.SecurityLevel > SecurityLevel.User ? (byte)1 : (byte)0)
                .Value(dest => dest.Unk3, (byte)0x58);


            Mapper.Register<PlayerItem, Data.P2P.ItemDto>()
                .Function(dest => dest.ItemNumber, src => src?.ItemNumber ?? 0);

            Mapper.Register<RoomCreationOptions, ChangeRuleDto>()
                .Member(dest => dest.Name, src => src.Name)
                .Member(dest => dest.Player_Limit, src => src.PlayerLimit)
                .Member(dest => dest.Password, src => src.Password)
                .Function(dest => dest.GameRule, src => (int)src.GameRule)
                .Member(dest => dest.Time, src => src.TimeLimit.TotalMinutes)
                .Member(dest => dest.Points, src => src.ScoreLimit)
                .Member(dest => dest.Map_ID, src => src.MapID)
                .Member(dest => dest.HasSpectator, src => src.hasSpectator)
                .Member(dest => dest.SpectatorLimit, src => src.Spectator)
                .Member(dest => dest.FMBurnMode, src => src.GetFMBurnModeInfo())
                .Member(dest => dest.Weapon_Limit, src => src.ItemLimit);
                //.Value(dest => dest.Unk1, 0)
                //.Value(dest => dest.Unk3, 0)
                //.Value(dest => dest.Unk4, 0)
                //.Value(dest => dest.Unk5, 0)
                //.Value(dest => dest.Unk6, 0)
                //.Value(dest => dest.Unk7, 0)
                //.Value(dest => dest.Unk8, 0)
                //.Value(dest => dest.Unk9, 0);

            Mapper.Register<Mail, NoteDto>()
                .Function(dest => dest.ReadCount, src => src.IsNew ? 0 : 1)
                .Function(dest => dest.DaysLeft,
                    src => DateTimeOffset.Now < src.Expires ? (src.Expires - DateTimeOffset.Now).TotalDays : 0);

            Mapper.Register<Mail, NoteContentDto>()
                .Member(dest => dest.Id, src => src.Id)
                .Member(dest => dest.Message, src => src.Message);

            Mapper.Register<PlayerItem, ItemDurabilityInfoDto>()
                .Member(dest => dest.ItemId, src => src.Id)
                .Function(dest => dest.Durabilityloss, src =>
                {
                    var loss = src.DurabilityLoss;
                    src.DurabilityLoss = 0;
                    return loss;
                });

            Mapper.Register<Player, PlayerInfoShortDto>()
                .Member(dest => dest.AccountId, src => src.Account.Id)
                .Member(dest => dest.Nickname, src => src.Account.Nickname)
                .Member(dest => dest.IsGM, src => (src.Account.SecurityLevel > SecurityLevel.User))
                .Function(dest => dest.TotalExp, src => src.TotalExperience);

            Mapper.Register<Player, PlayerLocationDto>()
                .Function(dest => dest.ServerGroupId, src => (int) Config.Instance.Id)
                .Function(dest => dest.ChannelId, src => src.Channel != null ? src.Channel.Id : -1)
                .Function(dest => dest.RoomId,
                    src => src.Room?.Id > 1 ? (int) src.Room?.Id : 0) // ToDo: Tutorial, License
                .Function(dest => dest.GameServerId, src => 0) // TODO Server ids
                .Function(dest => dest.ChatServerId, src => 0);

            Mapper.Register<Player, PlayerInfoDto>()
                .Function(dest => dest.Info, src => src.Map<Player, PlayerInfoShortDto>())
                .Function(dest => dest.Location, src => src.Map<Player, PlayerLocationDto>());

            Mapper.Register<Player, UserDataDto>()
                .Member(dest => dest.TotalExp, src => src.TotalExperience)
                .Member(dest => dest.AccountId, src => src.Account.Id)
                .Member(dest => dest.Nickname, src => src.Account.Nickname)
                .Member(dest => dest.PlayTime, src => TimeSpan.Parse(src.PlayTime))
                .Member(dest => dest.TotalGames, src => src.TotalMatches)
                .Member(dest => dest.GamesWon, src => src.TotalWins)
                .Member(dest => dest.GamesLost, src => src.TotalLosses)
                .Member(dest => dest.Level, src => src.Level);

            Mapper.Register<Player, PlayerNameTagInfoDto>()
                .Member(dest => dest.AccountId, src => src.Account.Id);

            Mapper.Register<Player, MyInfoDto>()
                .Member(dest => dest.Id, src => src.Club != null ? src.Club.Clan_ID : 0)
                .Member(dest => dest.Name, src => src.Club != null ? src.Club.Clan_Name : "")
                .Member(dest => dest.MemberCount, src => src.Club != null ? src.Club.Count : 0)
                .Member(dest => dest.Type, src => src.Club != null ? src.Club.Clan_Icon : "")
                .Member(dest => dest.State, src => src.Club != null ? src.Club[src.Account.Id].State : 0);

            Mapper.Register<Player, PlayerClubInfoDto>()
                .Member(dest => dest.Id, src => src.Club != null ? src.Club.Clan_ID : 0)
                .Member(dest => dest.Name, src => src.Club != null ? src.Club.Clan_Name : "")
                .Member(dest => dest.Type, src => src.Club != null ? src.Club.Clan_Icon : "");

            Mapper.Register<Player, ClubMemberDto>()
                .Member(dest => dest.AccountId, src => src.Account.Id)
                .Member(dest => dest.Nickname, src => src.Account.Nickname);



            Mapper.Compile(CompilationTypes.Source);
        }

#region Events

        protected override void OnStarted()
        {
            ResourceCache.PreCache();
            _worker.Start();
            _serverlistManager.Start();
        }

        protected override void OnStopping()
        {
            _worker.Stop(new TimeSpan(0));
            _serverlistManager.Dispose();
        }

        protected override void OnDisconnected(ProudSession session)
        {
            try
            {
                var gameSession = (GameSession)session;
                if (gameSession.Player != null)
                {
                    gameSession.Player.Room?.Leave(gameSession.Player);
                    gameSession.Player.Channel?.Leave(gameSession.Player);

                    gameSession.Player.Save();

                    PlayerManager.Remove(gameSession.Player);

                    Logger.ForAccount(gameSession)
                        .Debug($"Client {session.RemoteEndPoint} disconnected");

                    if (gameSession.Player.ChatSession != null)
                    {
                        gameSession.Player.ChatSession.GameSession = null;
                        gameSession.Player.ChatSession.Dispose();
                    }

                    if (gameSession.Player.RelaySession != null)
                    {
                        gameSession.Player.RelaySession.GameSession = null;
                        gameSession.Player.RelaySession.Dispose();
                    }

                    gameSession.Player.Session = null;
                    gameSession.Player.ChatSession = null;
                    gameSession.Player.RelaySession = null;
                    gameSession.Player = null;
                }

            }
            catch (Exception e)
            {
                Logger.Error(e.ToString());
            }
            base.OnDisconnected(session);
        }

        protected override void OnError(ErrorEventArgs e)
        {
            var gameSession = (GameSession) e.Session;
            var log = Logger;
            if (e.Session != null)
                log = log.ForAccount((GameSession) e.Session);

            if (e.Exception.ToString().Contains("opcode") || e.Exception.ToString().Contains("Bad format in"))
            {
                log.Warning(e.Exception.InnerException.Message);
                gameSession?.SendAsync(new ServerResultAckMessage(ServerResult.ServerError));
            }
            else if (gameSession.Player != null && (gameSession.Player.Room != null &&
                                                    gameSession.Player.Room.GameRuleManager.GameRule.StateMachine
                                                        .State == GameRuleState.Waiting
                                                    || gameSession.Player.Room == null))
            {
                log.Warning(e.Exception.InnerException.Message);
                gameSession?.SendAsync(new ServerResultAckMessage(ServerResult.ServerError));
            }
            else
            {
                log.Error(e.Exception, "Unhandled server error");
            }
            base.OnError(e);
        }

        //private void OnUnhandledMessage(object sender, MessageReceivedEventArgs e)
        //{
        //    var session = (GameSession)e.Session;
        //    Log.Warning()
        //        .Account(session)
        //        .Message($"Unhandled message {e.Message.GetType().Name}")
        //        .Write();
        //}

#endregion
    }
}
