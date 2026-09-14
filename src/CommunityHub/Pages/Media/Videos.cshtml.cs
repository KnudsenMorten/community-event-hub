using CommunityHub.Auth;
using CommunityHub.Core.Integrations.Graphics;

namespace CommunityHub.Pages.Media;

/// <summary>§1078 — "Video upload management": the media crew's video library.</summary>
public class VideosModel : MediaLibraryPageModel
{
    public VideosModel(MediaLibraryService library, ICurrentParticipantAccessor participant)
        : base(library, participant) { }

    public override MediaLibraryKind Kind => MediaLibraryKind.Video;
    public override string Heading => "Video upload management";
    public override string WhatBelongsHere =>
        "Video from the event — session recordings, interviews, b-roll and clips for social media.";
}
