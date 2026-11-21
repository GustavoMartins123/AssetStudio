namespace AssetStudio
{
    public sealed class Cubemap : Texture
    {
        public Texture2D Image { get; }

        public Cubemap(ObjectReader reader) : base(reader)
        {
            Image = new Texture2D(reader);
        }
    }
}
