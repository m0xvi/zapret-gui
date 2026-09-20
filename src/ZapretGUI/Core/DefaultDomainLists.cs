using System;

namespace ZapretGui.Core
{
    /// <summary>
    /// Встроенные эталонные наборы доменов для YouTube, Discord, Google и ключевых заблокированных сервисов.
    /// Используются для начального заполнения (auto-seeding) при первом запуске или если файлы списков пусты,
    /// обеспечивая отказоустойчивость и совместимость со всеми версиями движка Flowseal (включая 1.10.3+).
    /// </summary>
    public static class DefaultDomainLists
    {
        public static readonly string[] YoutubeDomains = new[]
        {
            "youtube.com",
            "www.youtube.com",
            "m.youtube.com",
            "music.youtube.com",
            "gaming.youtube.com",
            "kids.youtube.com",
            "studio.youtube.com",
            "accounts.youtube.com",
            "youtu.be",
            "yt.be",
            "ytimg.com",
            "i.ytimg.com",
            "s.ytimg.com",
            "i1.ytimg.com",
            "yt3.ggpht.com",
            "yt4.ggpht.com",
            "googlevideo.com",
            "redirector.googlevideo.com",
            "youtubei.googleapis.com",
            "yt-video-upload.l.google.com",
            "play.google.com",
            "googleapis.com",
            "googleusercontent.com",
            "gstatic.com",
            "widevine.com",
            "jnn-pa.googleapis.com",
            "youtube-nocookie.com",
            "youtube-ui.l.google.com"
        };

        public static readonly string[] DiscordDomains = new[]
        {
            "discord.com",
            "www.discord.com",
            "cdn.discordapp.com",
            "gateway.discord.gg",
            "discord.gg",
            "discordapp.com",
            "discordapp.net",
            "discord.media",
            "discord.co",
            "discord.design",
            "discord.dev",
            "discord.gift",
            "discord.new",
            "discord.store",
            "discord.tools",
            "dis.gd",
            "discord-attachments-uploads-prd.storage.googleapis.com",
            "status.discord.com",
            "discordstatus.com",
            "latency.discord.media",
            "media.discordapp.net",
            "images-ext-1.discordapp.net",
            "images-ext-2.discordapp.net"
        };

        public static readonly string[] GoogleDomains = new[]
        {
            "google.com",
            "www.google.com",
            "play.google.com",
            "googleapis.com",
            "gstatic.com",
            "googleusercontent.com",
            "googlevideo.com",
            "youtube.com",
            "www.youtube.com",
            "ytimg.com",
            "i.ytimg.com",
            "yt3.ggpht.com",
            "recaptcha.net",
            "widevine.com",
            "jnn-pa.googleapis.com",
            "gvt1.com",
            "1e100.net"
        };

        public static readonly string[] GeneralDomains = new[]
        {
            "youtube.com",
            "www.youtube.com",
            "youtu.be",
            "ytimg.com",
            "i.ytimg.com",
            "googlevideo.com",
            "yt3.ggpht.com",
            "discord.com",
            "www.discord.com",
            "cdn.discordapp.com",
            "gateway.discord.gg",
            "discord.gg",
            "discordapp.com",
            "discordapp.net",
            "discord.media",
            "dis.gd",
            "instagram.com",
            "www.instagram.com",
            "cdninstagram.com",
            "facebook.com",
            "www.facebook.com",
            "fbcdn.net",
            "twitter.com",
            "x.com",
            "twimg.com",
            "notion.so",
            "spotify.com",
            "openai.com",
            "chatgpt.com",
            "oaistatic.com",
            "oaiusercontent.com",
            "anthropic.com",
            "claude.ai",
            "arena.ai",
            "lmarena.ai",
            "rutracker.org",
            "nnmclub.to",
            "flibusta.is",
            "torproject.org",
            "proton.me",
            "protonmail.com",
            "hdrezka.ag",
            "rezka.ag"
        };
    }
}
