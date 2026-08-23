using Xunit;

// The app's language is deliberately global state — one setting drives the desktop,
// the tray and everything the bot says — so tests that switch it cannot run beside
// tests that read it. The whole suite finishes in about a second, so serialising it
// costs nothing and removes an entire class of flake.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
