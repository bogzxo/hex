namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    partial void LinkReadSessionNotice(HexDocument tab, ref LinkSessionNotice? session)
    {
        if (!linkLiveDocuments.TryGetValue(tab, out var shared))
            return;
        string people = string.Join(", ", shared.Participants.Where(each => each.Author != shared.Replica.Author)
            .Select(each => OneLine(each.Name, 45)).Where(name => name.Length > 0).Distinct().Take(6));
        bool follow = shared.Participants.Any(each => each.Author != shared.Replica.Author && each.Caret is not null);
        string owner = shared.Authority.DeviceId;
        string? ownerName = shared.Participants.Where(each => each.Device == owner).Select(each => each.Name).FirstOrDefault();
        session = new LinkSessionNotice(shared.Wire, people, tab.Editor.ReadOnly, shared.Offline, shared.Draft?.Reason, follow,
            owner, OneLine(LinkNameOf(owner, ownerName ?? owner), 65));
    }

    partial void LinkReadSessionPeople(HexDocument tab, ref IReadOnlyList<LinkSessionPerson> people)
    {
        if (!linkLiveDocuments.TryGetValue(tab, out var shared) || shared.Offline || shared.Draft is not null)
            return;
        people = [.. shared.Participants.Where(each => each.Author != shared.Replica.Author && each.Caret is not null)
            .DistinctBy(each => each.Device).Select(each => new LinkSessionPerson(each.Device, OneLine(each.Name, 65)))];
    }
}
