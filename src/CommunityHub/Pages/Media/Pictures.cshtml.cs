using CommunityHub.Auth;
using CommunityHub.Core.Integrations.Graphics;

namespace CommunityHub.Pages.Media;

/// <summary>§1078 — "Picture upload management": the media crew's picture library.</summary>
public class PicturesModel : MediaLibraryPageModel
{
    public PicturesModel(MediaLibraryService library, ICurrentParticipantAccessor participant)
        : base(library, participant) { }

    public override MediaLibraryKind Kind => MediaLibraryKind.Pictures;
    public override string Heading => "Picture upload management";
    public override string WhatBelongsHere =>
        "Photographs from the event — press pictures, session and booth shots, portraits.";
}
