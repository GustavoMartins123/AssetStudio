using System;
using System.Collections.Generic;
using UnityEngine;

public interface IFixtureShape { }

[Serializable]
public class FixtureCircle : IFixtureShape
{
    public float radius = 2.5f;
    public string label = "circle";
}

[Serializable]
public class FixtureSquare : IFixtureShape
{
    public int side = 7;
    public List<int> values = new List<int> { 1, 2, 3 };
}

[Serializable]
public struct FixtureNested
{
    public int id;
    public Vector2 pos;
    public string tag;
}

public enum FixtureMode { Alpha = 1, Beta = 5, Gamma = 9 }

public class FixtureBehaviour : MonoBehaviour
{
    public int intValue = 42;
    public float floatValue = 3.25f;
    public double doubleValue = 1.0 / 3.0;
    public long longValue = -1234567890123L;
    public bool flag = true;
    public byte smallByte = 200;
    public string text = "hello fixture";
    public Vector3 vec = new Vector3(1, 2, 3);
    public Quaternion rot = Quaternion.Euler(10, 20, 30);
    public Color color = Color.magenta;
    public Rect rect = new Rect(1, 2, 3, 4);
    public FixtureMode mode = FixtureMode.Beta;
    public List<string> names = new List<string> { "a", "b" };
    public FixtureNested nested = new FixtureNested { id = 9, pos = new Vector2(4, 5), tag = "nested" };
    public FixtureNested[] nestedArray = new FixtureNested[0];
    public Texture2D tex;
    public Material mat;
    public AnimationCurve curve = AnimationCurve.Linear(0, 0, 1, 2);
    public Gradient gradient = new Gradient();
    [SerializeReference] public IFixtureShape shape;
    [SerializeReference] public List<IFixtureShape> shapes = new List<IFixtureShape>();
}
