using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Callbacks;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Dialog;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using System.Linq;
using System.Reflection;

namespace DailyReward;

/// <summary>
/// Mod metadata - required for all mods
/// </summary>
public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.qingchun.dailyreward";
    public override string Name { get; init; } = "DailyReward";
    public override string Author { get; init; } = "qingchun";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new("1.0.0");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.11");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; } = "https://github.com/qingchunnh/DailyReward";
    public override bool? IsBundleMod { get; init; } = false;
    public override string License { get; init; } = "MIT";
}

/// <summary>
/// Configuration class for daily rewards
/// </summary>
public class DailyRewardConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Message text content displayed in the mail
    /// </summary>
    public string MessageText { get; set; } = "Here is your daily login reward!";
    /// <summary>
    /// Mail expiration time in hours
    /// </summary>
    public int CollectionTimeHours { get; set; } = 48;
    /// <summary>
    /// Minimum number of items to give as reward
    /// </summary>
    public int MinRewardCount { get; set; } = 3;
    /// <summary>
    /// Maximum number of items to give as reward
    /// </summary>
    public int MaxRewardCount { get; set; } = 5;
    /// <summary>
    /// Whether reward items are marked as found in raid
    /// </summary>
    public bool FoundInRaid { get; set; } = true;
    /// <summary>
    /// Item pool with weights (itemTpl -> weight), higher weight = more common
    /// </summary>
    public Dictionary<string, int> RewardTplPool { get; set; } = new();
}

/// <summary>
/// All players reward data stored in single JSON file
/// </summary>
public class DailyRewardData
{
    public Dictionary<string, PlayerRewardData> Players { get; set; } = new();
}

/// <summary>
/// Single player reward data
/// </summary>
public class PlayerRewardData
{
    public string PlayerName { get; set; } = "";
    /// <summary>
    /// Last time player received reward
    /// </summary>
    public DateTime LastRewardDate { get; set; } = DateTime.MinValue;
    public int TotalRewardsReceived { get; set; } = 0;
}

/// <summary>
/// Main mod class - hooks into profile loading to check and give daily rewards
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PreSptModLoader)]
public class DailyRewardMod : IOnLoad
{
    private readonly ISptLogger<DailyRewardMod> _logger;
    private readonly ModHelper _modHelper;
    private readonly SaveServer _saveServer;
    private readonly MailSendService _mailSendService;
    private readonly ItemHelper _itemHelper;
    private readonly DatabaseService _databaseService;
    private readonly JsonUtil _jsonUtil;

    private DailyRewardConfig? _config;
    private DailyRewardData? _data;
    private string? _modDirectory;
    private string? _dataFilePath;

    public DailyRewardMod(
        ISptLogger<DailyRewardMod> logger,
        ModHelper modHelper,
        SaveServer saveServer,
        MailSendService mailSendService,
        ItemHelper itemHelper,
        DatabaseService databaseService,
        JsonUtil jsonUtil)
    {
        _logger = logger;
        _modHelper = modHelper;
        _saveServer = saveServer;
        _mailSendService = mailSendService;
        _itemHelper = itemHelper;
        _databaseService = databaseService;
        _jsonUtil = jsonUtil;
    }

    public Task OnLoad()
    {
        // Load configuration
        _modDirectory = _modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        _config = _modHelper.GetJsonDataFromFile<DailyRewardConfig>(_modDirectory, "config.json");

        if (_config == null)
        {
            return Task.CompletedTask;
        }

        if (!_config.Enabled)
        {
            _logger.Warning("[DailyReward] Mod disabled in config");
            return Task.CompletedTask;
        }

        // Load or create data file
        _dataFilePath = System.IO.Path.Combine(_modDirectory, "data.json");
        LoadData();

        // Enable Harmony patch to intercept profile loading
        new GetProfileDataPatch(this, _logger).Enable();

        _logger.Success("[DailyReward] Mod loaded successfully");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Load data from JSON file
    /// </summary>
    private void LoadData()
    {
        try
        {
            if (File.Exists(_dataFilePath))
            {
                var json = File.ReadAllText(_dataFilePath);
                _data = _jsonUtil.Deserialize<DailyRewardData>(json);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"[DailyReward] Failed to load data: {ex.Message}");
        }

        _data ??= new DailyRewardData();
    }

    /// <summary>
    /// Save data to JSON file
    /// </summary>
    private void SaveData()
    {
        try
        {
            if (_data != null && _dataFilePath != null)
            {
                var json = _jsonUtil.Serialize(_data, true);
                File.WriteAllText(_dataFilePath, json);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"[DailyReward] Failed to save data: {ex.Message}");
        }
    }

    /// <summary>
    /// Check if player can receive reward today (compares dates only)
    /// </summary>
    private bool CanReceiveReward(PlayerRewardData playerData)
    {
        var today = DateTime.Now.Date;
        return playerData.LastRewardDate.Date != today;
    }

    /// <summary>
    /// Check and give daily reward to player
    /// Called when player logs in
    /// </summary>
    public void CheckAndGiveDailyReward(MongoId sessionId)
    {
        if (_config == null)
        {
            _logger.Warning("[DailyReward] Config is null");
            return;
        }

        if (!_config.Enabled)
        {
            return;
        }

        if (_data == null)
        {
            _logger.Warning("[DailyReward] Data is null");
            return;
        }

        try
        {
            var profile = _saveServer.GetProfile(sessionId);
            if (profile?.CharacterData?.PmcData == null)
            {
                return;
            }

            var pmcData = profile.CharacterData.PmcData;
            var playerName = pmcData.Info?.Nickname ?? "Unknown";

            // Skip if player hasn't created character yet (name is Unknown during character creation)
            if (playerName == "Unknown")
            {
                return;
            }

            var sessionIdStr = sessionId.ToString();

            // Get or create player data
            if (!_data.Players.TryGetValue(sessionIdStr, out var playerData))
            {
                playerData = new PlayerRewardData
                {
                    PlayerName = playerName,
                    LastRewardDate = DateTime.MinValue,
                    TotalRewardsReceived = 0
                };
                _data.Players[sessionIdStr] = playerData;
            }

            // Check if can receive reward today
            if (!CanReceiveReward(playerData))
            {
                return;
            }

            // Generate reward items
            var rewardItems = GenerateRewardItems();
            if (rewardItems.Count == 0)
            {
                _logger.Warning($"[DailyReward] No items generated for {playerName}({sessionIdStr}), check reward pool");
                return;
            }

            try
            {
                // Convert hours to seconds for mail expiration
                var maxStorageTimeSeconds = _config.CollectionTimeHours * 3600;

                // Create system message with reward items
                var messageDetails = new SendMessageDetails
                {
                    RecipientId = sessionId,
                    Sender = MessageType.SystemMessage,
                    MessageText = _config.MessageText,
                    Items = [],
                    ItemsMaxStorageLifetimeSeconds = maxStorageTimeSeconds
                };

                // Add items to message
                var rootItemParentId = new MongoId();
                messageDetails.Items.AddRange(rewardItems.AdoptOrphanedItems(rootItemParentId));

                // Send the message
                _mailSendService.SendMessageToPlayer(messageDetails);

                // Update player data with today's date
                playerData.LastRewardDate = DateTime.Now;
                playerData.TotalRewardsReceived++;
                playerData.PlayerName = playerName;
                SaveData();

                var itemNames = string.Join(", ", rewardItems.Select(i => i.Template));
                _logger.Success($"[DailyReward] {playerName}({sessionIdStr}) claimed {rewardItems.Count} items: [{itemNames}]");
            }
            catch (Exception ex)
            {
                _logger.Error($"[DailyReward] Failed to send reward to {playerName}({sessionIdStr}): {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"[DailyReward] Error processing reward: {ex.Message}");
        }
    }

    /// <summary>
    /// Generate random reward items based on weighted pool
    /// Number of items is random between MinRewardCount and MaxRewardCount
    /// </summary>
    private List<Item> GenerateRewardItems()
    {
        var items = new List<Item>();

        if (_config?.RewardTplPool == null || _config.RewardTplPool.Count == 0)
        {
            return items;
        }

        // Calculate total weight
        var totalWeight = _config.RewardTplPool.Sum(r => r.Value);
        var random = new Random();

        // Determine reward count (random between min and max)
        int min = Math.Max(1, _config.MinRewardCount);
        int max = Math.Max(min, _config.MaxRewardCount);
        int rewardCount = random.Next(min, max + 1);

        // Generate items based on random count
        for (int i = 0; i < rewardCount; i++)
        {
            // Roll for a reward based on weight
            var roll = random.NextDouble() * totalWeight;
            var currentWeight = 0.0;

            foreach (var kvp in _config.RewardTplPool)
            {
                currentWeight += kvp.Value;
                if (roll <= currentWeight)
                {
                    var itemTpl = kvp.Key;

                    // Validate item exists in database
                    var itemTemplate = _databaseService.GetItems().GetValueOrDefault(itemTpl);
                    if (itemTemplate == null)
                    {
                        _logger.Warning($"[DailyReward] Item template not found: {itemTpl}");
                        continue;
                    }

                    // Create the item
                    var newItem = CreateItem(itemTpl, 1, _config.FoundInRaid);
                    if (newItem != null)
                    {
                        items.Add(newItem);
                    }
                    break;
                }
            }
        }

        return items;
    }

    /// <summary>
    /// Create an item with specified template
    /// </summary>
    private Item? CreateItem(string itemTpl, int count, bool foundInRaid)
    {
        try
        {
            var item = new Item
            {
                Id = new MongoId(),
                Template = itemTpl,
                ParentId = null,
                SlotId = "hideout"
            };

            // Add stack count if more than 1
            if (count > 1)
            {
                item.AddUpd();
                item.Upd!.StackObjectsCount = count;
            }

            // Set found in raid status
            if (foundInRaid)
            {
                item.AddUpd();
                item.Upd!.SpawnedInSession = true;
            }

            return item;
        }
        catch (Exception ex)
        {
            _logger.Error($"[DailyReward] Failed to create item {itemTpl}: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// Harmony patch for ProfileCallbacks.GetProfileData to hook into player login
/// </summary>
public class GetProfileDataPatch : AbstractPatch
{
    private static DailyRewardMod? _dailyRewardMod;
    private static ISptLogger<DailyRewardMod>? _logger;

    public GetProfileDataPatch(DailyRewardMod dailyRewardMod, ISptLogger<DailyRewardMod> logger)
    {
        _dailyRewardMod = dailyRewardMod;
        _logger = logger;
    }

    protected override MethodBase GetTargetMethod()
    {
        return typeof(ProfileCallbacks).GetMethod(nameof(ProfileCallbacks.GetProfileData))!;
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionID)
    {
        try
        {
            _dailyRewardMod?.CheckAndGiveDailyReward(sessionID);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[DailyReward] Patch error: {ex.Message}");
        }
    }
}
