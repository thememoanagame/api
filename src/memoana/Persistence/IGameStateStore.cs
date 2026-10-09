namespace memoana.Persistence;

public interface IGameStateStore
{
    IReadOnlyList<PersistedRoom> LoadRooms();
    void Save(PersistedRoom room);
}
