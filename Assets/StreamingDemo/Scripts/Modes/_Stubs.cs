namespace StreamingDemo
{
    // Temporary placeholders while the remaining modes are written.
    public abstract class StubMode : DemoMode
    {
        public override string GameRef => "(coming soon)";
        public override string Tagline => "Coming soon.";
        public override string[] HowItWorks => new string[0];
        protected override void OnEnter() { }
        public override void Tick(float dt) { }
    }
    public sealed class DiscreteLevelsMode : StubMode { public override string Number => "01"; public override string Title => "Levels"; public override string ShortName => "Levels"; }
    public sealed class ObjectStreamingMode : StubMode { public override string Number => "03"; public override string Title => "Objects"; public override string ShortName => "Objects"; }
    public sealed class RoomPortalMode : StubMode { public override string Number => "04"; public override string Title => "Rooms"; public override string ShortName => "Rooms"; }
    public sealed class ChunkGenerationMode : StubMode { public override string Number => "05"; public override string Title => "Chunks"; public override string ShortName => "Chunks"; }
    public sealed class QuadtreeLodMode : StubMode { public override string Number => "06"; public override string Title => "Quadtree"; public override string ShortName => "Quadtree"; }
    public sealed class WorldPartitionMode : StubMode { public override string Number => "07"; public override string Title => "Partition"; public override string ShortName => "Partition"; }
}
