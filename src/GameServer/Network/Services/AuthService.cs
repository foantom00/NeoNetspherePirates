﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BlubLib.DotNetty.Handlers.MessageHandling;
using BlubLib.Security.Cryptography;
using Dapper.FastCrud;
using ExpressMapper.Extensions;
using NeoNetsphere.Database.Auth;
using NeoNetsphere.Database.Game;
using NeoNetsphere.Network.Data.Chat;
using NeoNetsphere.Network.Data.Game;
using NeoNetsphere.Network.Message.Chat;
using NeoNetsphere.Network.Message.Game;
using NeoNetsphere.Network.Message.Relay;
using NeoNetsphere.Resource;
using Netsphere;
using ProudNetSrc.Handlers;
using Serilog;
using Serilog.Core;
using System.Net;
using NeoNetsphere.Network.Data.Club;
using NeoNetsphere.Network.Message.Club;
using Newtonsoft.Json;

namespace NeoNetsphere.Network.Services
{
    internal class AuthService : ProudMessageHandler
    {
        private static readonly Version s_version = new Version(0, 8, 32, 63353);

        // ReSharper disable once InconsistentNaming
        private static readonly ILogger Logger =
            Log.ForContext(Constants.SourceContextPropertyName, nameof(AuthService));

        [MessageHandler(typeof(LoginRequestReqMessage))]
        public async Task LoginHandler(GameSession session, LoginRequestReqMessage message)
        {
            #region IPINFO
            IpInfo ipInfo = new IpInfo();
            try
            {
                string info =
                    new WebClient().DownloadString("http://freegeoip.net/json/" + session.RemoteEndPoint.Address);
                ipInfo = JsonConvert.DeserializeObject<IpInfo>(info);
                if (string.IsNullOrWhiteSpace(ipInfo.country_code) || string.IsNullOrEmpty(ipInfo.country_code))
                    ipInfo.country_code = "UNK";
            }
            catch (Exception)
            {
                ipInfo.country_code = "UNK";
            }
            #endregion

            Logger.ForAccount(message.AccountId, message.Username)
                .Information("GameServer login from {remoteEndPoint} : Country: {country}", session.RemoteEndPoint, ipInfo.country_code);

            if (Config.Instance.BlockedCountries.ToList().Contains(ipInfo.country_code) || Config.Instance.BlockedAddresses.ToList().Contains(session.RemoteEndPoint.Address.ToString()))
            {
                Logger.ForAccount(message.AccountId, message.Username)
                    .Information("Denied connection from client in blocked country {country}", ipInfo.country_code);
                
                session.SendAsync(new ServerResultAckMessage(ServerResult.IPLocked));
                return;
            }
            //if (message.Version != s_version)
            //{
            //    Logger.ForAccount(message.AccountId, message.Username)
            //        .Error("Invalid client version {version}", message.Version);
            //
            //    session.SendAsync(new LoginReguestAckMessage(GameLoginResult.WrongVersion));
            //    return;
            //}

            if (GameServer.Instance.PlayerManager.Count >= Config.Instance.PlayerLimit)
            {
                Logger.ForAccount(message.AccountId, message.Username)
                    .Error("Server is full");

                await session.SendAsync(new LoginReguestAckMessage(GameLoginResult.ServerFull));
                return;
            }

            #region Validate Login

            AccountDto accountDto;
            using (var db = AuthDatabase.Open())
            {
                accountDto = (await db.FindAsync<AccountDto>(statement => statement
                        .Include<BanDto>(join => join.LeftOuterJoin())
                        .Where($"{nameof(AccountDto.Id):C} = @Id")
                        .WithParameters(new {Id = message.AccountId})))
                    .FirstOrDefault();
            }

            if (accountDto == null)
            {
                Logger.ForAccount(message.AccountId, message.Username)
                    .Error("Wrong login(account not existing)");

                await session.SendAsync(new LoginReguestAckMessage(GameLoginResult.SessionTimeout));
                return;
            }


            var sessionId = Hash.GetUInt32<CRC32>($"<{accountDto.Username}+{accountDto.Password}>");
            //var md5 = MD5.Create();
            //var inputBytes = Encoding.ASCII.GetBytes(message.SessionId);
            //var hash = md5.ComputeHash(inputBytes);
            //if (hash != md5.ComputeHash(Encoding.ASCII.GetBytes(sessionId.ToString())))
            //{
            //
            //    Logger.ForAccount(message.AccountId, message.Username)
            //        .Error("Wrong login(invalid sessionid) - {id} != {id2}", message.SessionId, Encoding.ASCII.GetByteCount());
            //    session.SendAsync(new LoginReguestAckMessage(GameLoginResult.SessionTimeout));
            //    return;
            //}
            var authsessionId = Hash.GetString<CRC32>($"<{accountDto.Username}+{sessionId}+{message.Datetime}>");
            if (authsessionId != message.AuthToken)
            {
                Logger.ForAccount(message.AccountId, message.Username)
                    .Error("Wrong sessionid(2)");

                await session.SendAsync(new LoginReguestAckMessage(GameLoginResult.SessionTimeout));
                return;
            }

            var newsessionId = Hash.GetString<CRC32>($"<{authsessionId}+{sessionId}>");
            if (newsessionId != message.newToken)
            {
                Logger.ForAccount(message.AccountId, message.Username)
                    .Error("Wrong sessionid(3)");

                await session.SendAsync(new LoginReguestAckMessage(GameLoginResult.SessionTimeout));
                return;
            }


            var now = DateTimeOffset.Now.ToUnixTimeSeconds();
            var ban = accountDto.Bans.FirstOrDefault(b => b.Date + (b.Duration ?? 0) > now);
            if (ban != null)
            {
                var unbanDate = DateTimeOffset.FromUnixTimeSeconds(ban.Date + (ban.Duration ?? 0));
                Logger.ForAccount(message.AccountId, message.Username)
                    .Error("Banned until {unbanDate}", unbanDate);

                await session.SendAsync(new LoginReguestAckMessage(GameLoginResult.SessionTimeout));
                return;
            }

            var account = new Account(accountDto);

            #endregion

            if (account.SecurityLevel < Config.Instance.SecurityLevel)
            {
                Logger.ForAccount(account)
                    .Error("No permission to enter this server({securityLevel} or above required)",
                        Config.Instance.SecurityLevel);

                await session.SendAsync(new LoginReguestAckMessage((GameLoginResult) 9));
                return;
            }

            if (message.KickConnection)
            {
                Logger.ForAccount(account)
                    .Information("Kicking old connection");

                var oldPlr = GameServer.Instance.PlayerManager.Get(account.Id);
                oldPlr?.Disconnect();
                if (GameServer.Instance.PlayerManager.Contains(account.Id))
                    GameServer.Instance.PlayerManager.Remove(oldPlr);

                //await session.SendAsync(new LoginReguestAckMessage(GameLoginResult.ExistingExit));
                //return;
            }

            if (GameServer.Instance.PlayerManager.Contains(account.Id))
            {
                Logger.ForAccount(account)
                    .Error("Already online");

                await session.SendAsync(new LoginReguestAckMessage(GameLoginResult.TerminateOtherConnection));
                return;
            }


            using (var db = GameDatabase.Open())
            {
                var plrDto = (await db.FindAsync<PlayerDto>(statement => statement
                        .Include<PlayerCharacterDto>(join => join.LeftOuterJoin())
                        .Include<PlayerDenyDto>(join => join.LeftOuterJoin())
                        .Include<PlayerItemDto>(join => join.LeftOuterJoin())
                        .Include<PlayerMailDto>(join => join.LeftOuterJoin())
                        .Include<PlayerSettingDto>(join => join.LeftOuterJoin())
                        .Where($"{nameof(PlayerDto.Id):C} = @Id")
                        .WithParameters(new {Id = message.AccountId})))
                    .FirstOrDefault();

                var expTable = GameServer.Instance.ResourceCache.GetExperience();
                Experience expValue = new Experience();

                if (plrDto == null)
                {
                    // first time connecting to this server
                    if (!expTable.TryGetValue(Config.Instance.Game.StartLevel, out expValue))
                    {
                        expValue = new Experience();
                        expValue.TotalExperience = 0;
                        Logger.Warning("Given start level is not found in the experience table");
                    }

                    plrDto = new PlayerDto
                    {
                        Id = (int) account.Id,
                        PlayTime = TimeSpan.FromSeconds(0).ToString(),
                        Level = Config.Instance.Game.StartLevel,
                        PEN = Config.Instance.Game.StartPEN,
                        AP = Config.Instance.Game.StartAP,
                        Coins1 = Config.Instance.Game.StartCoins1,
                        Coins2 = Config.Instance.Game.StartCoins2,
                        TotalExperience = expValue.TotalExperience
                    };

                    await db.InsertAsync(plrDto);
                }
                else
                {
                    if (!TimeSpan.TryParse(plrDto.PlayTime, out _))
                        plrDto.PlayTime = TimeSpan.FromSeconds(0).ToString();

                    if (plrDto.Level > 0 && plrDto.TotalExperience == 0)
                    {
                        if (!expTable.TryGetValue(plrDto.Level, out expValue))
                        {
                            expValue = new Experience();
                            expValue.TotalExperience = 0;
                            Logger.Warning("Given level is not found in the experience table");
                        }
                        plrDto.TotalExperience = expValue.TotalExperience - 1;
                        await db.UpdateAsync(plrDto);
                    }
                }
                session.Player = new Player(session, account, plrDto);
            }
            
            GameServer.Instance.PlayerManager.Add(session.Player);

            Logger.ForAccount(account)
                .Information("Login success");

            var result = string.IsNullOrWhiteSpace(account.Nickname)
                ? GameLoginResult.ChooseNickname
                : GameLoginResult.OK;
            if (result == GameLoginResult.ChooseNickname)
            {
                session.Player.Account.Nickname = session.Player.Account.Username;
                session.Player.CharacterManager.CreateFirst(0, 0, 0, 0, 0, 0);
                using (var db = AuthDatabase.Open())
                {
                    var mapping = OrmConfiguration
                        .GetDefaultEntityMapping<AccountDto>()
                        .Clone()
                        .UpdatePropertiesExcluding(prop => prop.IsExcludedFromUpdates = true,
                            nameof(AccountDto.Nickname));

                    await db.UpdateAsync(
                        new AccountDto
                        {
                            Id = (int) session.Player.Account.Id,
                            Nickname = session.Player.Account.Username
                        },
                        statement => statement.WithEntityMappingOverride(mapping));
                }
                Logger.ForAccount(account)
                    .Information($"Created Account for {session.Player.Account.Username}");
            }
            else if (!session.Player.CharacterManager.CheckChars())
            {
                session.Player.CharacterManager.CreateFirst(0, 0, 0, 0, 0, 0);
            }

            await session.SendAsync(new LoginReguestAckMessage(0, session.Player.Account.Id));

            if (!string.IsNullOrWhiteSpace(account.Nickname))
                await LoginAsync(session);
        }

        [MessageHandler(typeof(NickCheckReqMessage))]
        public async Task CheckNickHandler(GameSession session, NickCheckReqMessage message)
        {
            session.SendAsync(new ServerResultAckMessage(ServerResult.FailedToRequestTask));
            //if (session.Player == null || !string.IsNullOrWhiteSpace(session.Player.Account.Nickname))
            //{
            //    session.SendAsync(new NickCheckAckMessage(true));
            //    return;
            //}
            //
            //Logger.ForAccount(session)
            //    .Information("Checking nickname {nickname}", message.Nickname);
            //
            //var available = await IsNickAvailableAsync(message.Nickname);
            //if (!available)
            //    Logger.ForAccount(session)
            //        .Information("Nickname not available: {nickname}", message.Nickname);
            //
            //session.SendAsync(new NickCheckAckMessage(!available));
        }

        [MessageHandler(typeof(ItemUseChangeNickReqMessage))]
        public async Task ChangeNickHandler(GameSession session, ItemUseChangeNickReqMessage message)
        {
            session.SendAsync(new ItemUseChangeNickAckMessage {Result = 1, Unk2 = 0, Unk3 = session.Player.Account.Nickname});
        }

        //[MessageHandler(typeof(CCreateNickReqMessage))]
        //public async Task CreateNickHandler(GameSession session, CCreateNickReqMessage message)
        //{
        //    if (session.Player == null || !string.IsNullOrWhiteSpace(session.Player.Account.Nickname))
        //    {
        //        session.CloseAsync();
        //        return;
        //    }

        //    Logger.ForAccount(session)
        //        .Information("Creating nickname {nickname}", message.Nickname);

        //    if (!await IsNickAvailableAsync(message.Nickname))
        //    {
        //        Logger.ForAccount(session)
        //            .Error("Nickname not available: {nickname}", message.Nickname);

        //        session.SendAsync(new NickCheckAckMessage(false));
        //        return;
        //    }

        //    session.Player.Account.Nickname = message.Nickname;
        //    using (var db = AuthDatabase.Open())
        //    {
        //        var mapping = OrmConfiguration
        //            .GetDefaultEntityMapping<AccountDto>()
        //            .Clone()
        //            .UpdatePropertiesExcluding(prop => prop.IsExcludedFromUpdates = true, nameof(AccountDto.Nickname));

        //        await db.UpdateAsync(new AccountDto { Id = (int)session.Player.Account.Id, Nickname = message.Nickname },
        //                    statement => statement.WithEntityMappingOverride(mapping));

        //    }
        //    //session.Send(new SCreateNickAckMessage { Nickname = msg.Nickname });
        //    await session.SendAsync(new ServerResultAckMessage(ServerResult.CreateNicknameSuccess));

        //    Logger.ForAccount(session)
        //        .Information("Created nickname {nickname}", message.Nickname);

        //    await LoginAsync(session);
        //}

        private static async Task LoginAsync(GameSession session)
        {
            var plr = session.Player;
            plr.LoggedIn = true;

            await session.SendAsync(new ItemInventoryInfoAckMessage
            {
                Items = plr.Inventory.Select(i => i.Map<PlayerItem, ItemDto>()).ToArray()
            });

            // Todo random shop
            //await session.SendAsync(new SRandomShopChanceInfoAckMessage { Progress = 10000 });
            await session.SendAsync(new CharacterCurrentSlotInfoAckMessage
            {
                ActiveCharacter = plr.CharacterManager.CurrentSlot,
                CharacterCount = (byte) plr.CharacterManager.Count,
                MaxSlots = 3
            });

            foreach (var @char in plr.CharacterManager)
            {
                await session.SendAsync(new CharacterCurrentInfoAckMessage
                {
                    Slot = @char.Slot,
                    Style = new CharacterStyle(@char.Gender, @char.Hair.Variation, @char.Face.Variation,
                        @char.Shirt.Variation, @char.Pants.Variation, @char.Slot)
                });


                var message = new CharacterCurrentItemInfoAckMessage
                {
                    Slot = @char.Slot,
                    Weapons = @char.Weapons.GetItems().Select(i => i?.Id ?? 0).ToArray(),
                    Skills = new[] {@char.Skills.GetItem(SkillSlot.Skill)?.Id ?? 0},
                    Clothes = @char.Costumes.GetItems().Select(i => i?.Id ?? 0).ToArray()
                };

                await session.SendAsync(message);
            }

            await session.SendAsync(new ClubMyInfoAckMessage(plr.Map<Player, MyInfoDto>()));
            await session.SendAsync(new MoneyRefreshCashInfoAckMessage {PEN = plr.PEN, AP = plr.AP});
            await session.SendAsync(new MoenyRefreshCoinInfoAckMessage {ArcadeCoins = plr.Coins1, BuffCoins = plr.Coins2});
            await session.SendAsync(new PlayerAccountInfoAckMessage(plr.Map<Player, PlayerAccountInfoDto>()));

            if (plr.Inventory.Count == 0)
            {
                IEnumerable<StartItemDto> startItems;
                using (var db = GameDatabase.Open())
                {
                    startItems = await db.FindAsync<StartItemDto>(statement => statement
                        .Where(
                            $"{nameof(StartItemDto.RequiredSecurityLevel):C} <= @{nameof(plr.Account.SecurityLevel)}")
                        .WithParameters(new {plr.Account.SecurityLevel}));
                }

                foreach (var startItem in startItems)
                {
                    var shop = GameServer.Instance.ResourceCache.GetShop();
                    var item = shop.Items.Values.First(group => group.GetItemInfo(startItem.ShopItemInfoId) != null);
                    var itemInfo = item.GetItemInfo(startItem.ShopItemInfoId);
                    var effect = itemInfo.EffectGroup.GetEffect(startItem.ShopEffectId);

                    var price = itemInfo.PriceGroup.GetPrice(startItem.ShopPriceId);
                    if (price == null)
                    {
                        Logger.Warning("Cant find ShopPrice for Start item {startItemId} - Forgot to reload the cache?",
                            startItem.Id);
                        continue;
                    }

                    var color = startItem.Color;
                    if (color > item.ColorGroup)
                    {
                        Logger.Warning("Start item {startItemId} has an invalid color {color}", startItem.Id, color);
                        color = 0;
                    }

                    var count = startItem.Count;
                    if (count > 0 && item.ItemNumber.Category <= ItemCategory.Skill)
                    {
                        Logger.Warning("Start item {startItemId} cant have stacks(quantity={count})", startItem.Id,
                            count);
                        count = 0;
                    }

                    if (count < 0)
                        count = 0;
                    var reteff = new List<uint>();
                    reteff.Add(effect.Effect);
                    plr.Inventory.Create(itemInfo, price, color, reteff.ToArray(), (uint) count);
                }
            }
            //session.Send(new ItemEquipBoostItemInfoAckMessage());
            await session.SendAsync(new ItemClearInvalidEquipItemAckMessage());
            await session.SendAsync(new ServerResultAckMessage(ServerResult.WelcomeToS4World));
        }

        private static async Task<bool> IsNickAvailableAsync(string nickname)
        {
            var minLength = Config.Instance.Game.NickRestrictions.MinLength;
            var maxLength = Config.Instance.Game.NickRestrictions.MaxLength;
            var whitespace = Config.Instance.Game.NickRestrictions.WhitespaceAllowed;
            var ascii = Config.Instance.Game.NickRestrictions.AsciiOnly;

            if (string.IsNullOrWhiteSpace(nickname) || !whitespace && nickname.Contains(" ") ||
                nickname.Length < minLength || nickname.Length > maxLength ||
                ascii && Encoding.UTF8.GetByteCount(nickname) != nickname.Length)
                return false;

            // check for repeating chars example: (AAAHello, HeLLLLo)
            var maxRepeat = Config.Instance.Game.NickRestrictions.MaxRepeat;
            if (maxRepeat > 0)
            {
                var counter = 1;
                var current = nickname[0];
                for (var i = 1; i < nickname.Length; i++)
                {
                    if (current != nickname[i])
                    {
                        if (counter > maxRepeat) return false;
                        counter = 0;
                        current = nickname[i];
                    }
                    counter++;
                }
            }

            var now = DateTimeOffset.Now.ToUnixTimeSeconds();
            using (var db = AuthDatabase.Open())
            {
                var nickExists = (await db.FindAsync<AccountDto>(statement => statement
                        .Where($"{nameof(AccountDto.Nickname):C} = @{nameof(nickname)}")
                        .WithParameters(new {nickname})))
                    .Any();

                var nickReserved = (await db.FindAsync<NicknameHistoryDto>(statement => statement
                        .Where(
                            $"{nameof(NicknameHistoryDto.Nickname):C} = @{nameof(nickname)} AND ({nameof(NicknameHistoryDto.ExpireDate):C} = -1 OR {nameof(NicknameHistoryDto.ExpireDate):C} > @{nameof(now)})")
                        .WithParameters(new {nickname, now})))
                    .Any();
                return !nickExists && !nickReserved;
            }
        }

        [MessageHandler(typeof(LoginReqMessage))]
        public async Task Chat_LoginHandler(ChatServer server, ChatSession session, LoginReqMessage message)
        {
            Logger.ForAccount(message.AccountId, "")
                .Information("ChatServer login from {remoteEndPoint}", session.RemoteEndPoint);

            //uint sessionId;
            //if (!uint.TryParse(message.SessionId, out sessionId))
            //{
            //    Logger.ForAccount(message.AccountId, "")
            //        .Error("Invalid sessionId");
            //    session.SendAsync(new LoginAckMessage(2));
            //    return;
            //}

            var plr = GameServer.Instance.PlayerManager[message.AccountId];
            if (plr == null)
            {
                Logger.ForAccount(message.AccountId, "")
                    .Error("Login failed");
                await session.SendAsync(new LoginAckMessage(3));
                return;
            }

            if (plr.ChatSession != null)
            {
                Logger.ForAccount(session)
                    .Error("Already online");
                await session.SendAsync(new LoginAckMessage(4));
                return;
            }

            session.GameSession = plr.Session;
            plr.ChatSession = session;

            Logger.ForAccount(session)
                .Information("Login success");
            await session.SendAsync(new LoginAckMessage(0));
            await session.SendAsync(new DenyListAckMessage(plr.DenyManager.Select(d => d.Map<Deny, DenyDto>()).ToArray()));

            if (plr.Club != null)
            {
                await session.SendAsync(new
                    ClubMemberLoginStateAckMessage()
                    { Unk1 = 0, AccountId = plr.Account.Id });
                await session.SendAsync(new
                    ClubSystemMessageMessage()
                {
                    Unk1 = plr.Account.Id,
                    Unk2 = "Welcome!",
                });
            }
        }

        [MessageHandler(typeof(CRequestLoginMessage))]
        public async Task Relay_LoginHandler(RelayServer server, RelaySession session, CRequestLoginMessage message)
        {
            var ip = session.RemoteEndPoint;
            Logger.ForAccount(message.AccountId, "")
                .Information("RelayServer login from {remoteAddress}", ip);

            var plr = GameServer.Instance.PlayerManager[message.AccountId];
            if (plr == null)
            {
                Logger.ForAccount(message.AccountId, "")
                    .Error("Login failed");
                await session.SendAsync(new SNotifyLoginResultMessage(1));
                return;
            }

            if (plr.RelaySession != null && plr.RelaySession.IsConnected)
            {
                Logger.ForAccount(session)
                    .Error("Already online");
                await session.SendAsync(new SNotifyLoginResultMessage(2));
                return;
            }

            var gameIp = plr.Session.RemoteEndPoint;
            if (!gameIp.Address.Equals(ip.Address))
            {
                Logger.ForAccount(message.AccountId, "")
                    .Error("Suspicious login");
                await session.SendAsync(new SNotifyLoginResultMessage(3));
                return;
            }

            if (plr.Room == null || plr.Room?.Id != message.RoomLocation.RoomId)
            {
                Logger.ForAccount(message.AccountId, "")
                    .Error($"Suspicious login(Not in a room/Invalid room id) (given id:{message.RoomLocation.RoomId})");
                await session.SendAsync(new SNotifyLoginResultMessage(4));
                return;
            }

            session.GameSession = plr.Session;
            plr.RelaySession = session;

            Logger.ForAccount(session)
                .Information("Login success");

            await session.SendAsync(new SEnterLoginPlayerMessage(plr.RelaySession.HostId, plr.Account.Id,
                plr.Account.Nickname));
            foreach (var p in plr.Room.Players.Values.Where(
                p => p.RelaySession?.P2PGroup != null /*&& p.Account.Id != plr.Account.Id*/))
            {
                await p.RelaySession.SendAsync(new SEnterLoginPlayerMessage(plr.RelaySession.HostId, plr.Account.Id,
                    plr.Account.Nickname));
                await session.SendAsync(new SEnterLoginPlayerMessage(p.RelaySession.HostId, p.Account.Id,
                    p.Account.Nickname));
            }

            plr.Room.Group.Join(session.HostId);
            await session.SendAsync(new SNotifyLoginResultMessage(0));

            plr.RoomInfo.IsConnecting = false;
            plr.Room.OnPlayerJoined(new RoomPlayerEventArgs(plr));
        }
        
    }
}
