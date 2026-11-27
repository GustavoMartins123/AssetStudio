using UnityEngine;

public class FixtureData : ScriptableObject
{
    public int[] numbers = { 5, 6, 7 };
    public string title = "fixture data";
    [SerializeReference] public IFixtureShape single;
    public AnimationCurve curve = AnimationCurve.EaseInOut(0, 0, 1, 1);
}
