using System;

namespace statsCollector.Domain.Stats;

public sealed class EconomyStats
{
    private readonly Action _markDirty;
    
    private int _moneySpent;
    private int _equipmentValue;
    private int _itemsPurchased;
    private int _itemsPickedUp;
    private int _lossBonus;
    private int _roundStartMoney;
    private int _roundEndMoney;
    private int _equipmentValueStart;
    private int _equipmentValueEnd;

    public EconomyStats(Action markDirty)
    {
        _markDirty = markDirty;
    }

    public int MoneySpent { get => _moneySpent; set { _moneySpent = value; _markDirty(); } }
    public int EquipmentValue { get => _equipmentValue; set { _equipmentValue = value; _markDirty(); } }
    public int ItemsPurchased { get => _itemsPurchased; set { _itemsPurchased = value; _markDirty(); } }
    public int ItemsPickedUp { get => _itemsPickedUp; set { _itemsPickedUp = value; _markDirty(); } }
    public int LossBonus { get => _lossBonus; set { _lossBonus = value; _markDirty(); } }
    public int RoundStartMoney { get => _roundStartMoney; set { _roundStartMoney = value; _markDirty(); } }
    public int RoundEndMoney { get => _roundEndMoney; set { _roundEndMoney = value; _markDirty(); } }
    public int EquipmentValueStart { get => _equipmentValueStart; set { _equipmentValueStart = value; _markDirty(); } }
    public int EquipmentValueEnd { get => _equipmentValueEnd; set { _equipmentValueEnd = value; _markDirty(); } }

    public void Reset()
    {
        _moneySpent = _equipmentValue = _itemsPurchased = _itemsPickedUp = 0;
        _lossBonus = _roundStartMoney = _roundEndMoney = 0;
        _equipmentValueStart = _equipmentValueEnd = 0;
        _markDirty();
    }
}
