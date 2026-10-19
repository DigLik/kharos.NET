using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client.Audio;

/// <summary>
/// Обстановка для фона (Mood в ambience.ts): тревога и бой считает <see cref="Battle"/>, успех (открытая тема, отбитая
/// атака) — WorldEvents и Battle; фон (<see cref="AmbienceAudio"/>) подтягивает к ним свои значения медленно.
/// </summary>
internal sealed class Mood
{
    /// <summary>Тревога от 0 до 1: противник в виду, ракета на нас — единица.</summary>
    public double Threat { get; set; }

    /// <summary>Бой от 0 до 1: как часто стреляют свои и противник.</summary>
    public double Battle { get; set; }

    /// <summary>Сколько успехов было: растёт на единицу — светлые аккорды и фанфара.</summary>
    public int Triumphs { get; set; }

    /// <summary>Над своим местом метка вражеского удара.</summary>
    public bool Struck { get; set; }
}

/// <summary>
/// Кто противник: все игроки, кроме себя, ничьих (0) и союзников. Союз — общая глава (Team.head ≥ 0) у сущностей счёта
/// игроков, как alliesIn в TS.
/// </summary>
internal sealed class Teams
{
    private readonly HashSet<uint> _allies = [];
    private ComponentRef? _team;
    private double[] _players = new double[8], _heads = new double[8];

    /// <summary>Пересчитать союзников по реплике.</summary>
    public void Refresh(ClientContext c)
    {
        _team ??= c.Component("Team");
        _allies.Clear();
        var entities = c.Each(_team);
        if (_players.Length < entities.Length)
            (_players, _heads) = (new double[entities.Length * 2], new double[entities.Length * 2]);
        c.Column(entities, _team, "player", _players);
        c.Column(entities, _team, "head", _heads);
        var head = double.NaN;
        for (var i = 0; i < entities.Length; i++)
            if (_players[i] == c.Player)
                head = _heads[i];
        if (double.IsNaN(head) || head < 0)
            return;
        for (var i = 0; i < entities.Length; i++)
            if (_heads[i] == head && _players[i] != c.Player)
                _allies.Add((uint)_players[i]);
    }

    public bool Allied(uint player) => _allies.Contains(player);

    /// <summary>Противник ли игрок: не свой, не ничей и не союзник.</summary>
    public bool Foe(ClientContext c, uint player) => player != c.Player && player != 0 && !_allies.Contains(player);
}
