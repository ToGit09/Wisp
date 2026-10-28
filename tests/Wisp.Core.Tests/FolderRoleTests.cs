using Wisp.Core.Mail;
using Wisp.Core.Storage;
using Candidate = Wisp.Core.Mail.ImapMapping.RoleCandidate;

namespace Wisp.Core.Tests;

public class FolderRoleTests
{
    [Fact]
    public void Declared_drafts_folder_wins_over_a_leftover_with_a_known_name()
    {
        // e.g. "Entwürfe" found by name, "Drafts" marked \Drafts by the server
        var roles = ImapMapping.AssignUniqueRoles(
        [
            new Candidate(FolderRole.Drafts, Declared: false, Depth: 0),
            new Candidate(FolderRole.Drafts, Declared: true, Depth: 0),
        ]);

        Assert.Equal([FolderRole.None, FolderRole.Drafts], roles);
    }

    [Fact]
    public void Two_spam_folders_found_by_name_keep_only_the_shallowest()
    {
        var roles = ImapMapping.AssignUniqueRoles(
        [
            new Candidate(FolderRole.Junk, Declared: false, Depth: 1), // INBOX/Spam
            new Candidate(FolderRole.Junk, Declared: false, Depth: 0), // Junk
        ]);

        Assert.Equal([FolderRole.None, FolderRole.Junk], roles);
    }

    [Fact]
    public void Different_roles_and_normal_folders_are_left_alone()
    {
        var roles = ImapMapping.AssignUniqueRoles(
        [
            new Candidate(FolderRole.Inbox, true, 0),
            new Candidate(FolderRole.Drafts, true, 0),
            new Candidate(FolderRole.Junk, false, 0),
            new Candidate(FolderRole.None, false, 0),
            new Candidate(FolderRole.None, false, 1),
        ]);

        Assert.Equal([FolderRole.Inbox, FolderRole.Drafts, FolderRole.Junk, FolderRole.None, FolderRole.None], roles);
    }
}
