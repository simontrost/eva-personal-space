using UnityEngine;
using UnityEngine.UI;

[AddComponentMenu("")]
[RequireComponent(typeof(RectTransform), typeof(CanvasRenderer))]
public sealed class EVASelectionPreviewIcon : MaskableGraphic
{
    public enum Glyph { Play, Loading, Stop }
    private Glyph glyph;

    public void SetGlyph(Glyph value)
    {
        if (glyph == value) return;
        glyph = value;
        SetVerticesDirty();
    }

    private void Update()
    {
        if (glyph == Glyph.Loading) SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vertices)
    {
        vertices.Clear();
        Rect rect = GetPixelAdjustedRect();
        Vector2 center = rect.center;
        float size = Mathf.Min(rect.width, rect.height) * 0.24f;
        if (size <= 0f) return;
        if (glyph == Glyph.Play)
        {
            vertices.AddVert(center + new Vector2(-size * 0.65f, -size), color, Vector2.zero);
            vertices.AddVert(center + new Vector2(-size * 0.65f, size), color, Vector2.zero);
            vertices.AddVert(center + new Vector2(size, 0f), color, Vector2.zero);
            vertices.AddTriangle(0, 1, 2);
        }
        else if (glyph == Glyph.Stop)
        {
            vertices.AddVert(center + new Vector2(-size, -size), color, Vector2.zero);
            vertices.AddVert(center + new Vector2(-size, size), color, Vector2.zero);
            vertices.AddVert(center + new Vector2(size, size), color, Vector2.zero);
            vertices.AddVert(center + new Vector2(size, -size), color, Vector2.zero);
            vertices.AddTriangle(0, 1, 2);
            vertices.AddTriangle(0, 2, 3);
        }
        else
        {
            const int segments = 10;
            float rotation = -Time.unscaledTime * 240f;
            for (int i = 0; i < segments; i++)
            {
                float start = (rotation + i * 30f) * Mathf.Deg2Rad;
                float end = start + 24f * Mathf.Deg2Rad;
                Vector2 a = new Vector2(Mathf.Cos(start), Mathf.Sin(start));
                Vector2 b = new Vector2(Mathf.Cos(end), Mathf.Sin(end));
                Color tint = color;
                tint.a *= (i + 1f) / segments;
                int first = vertices.currentVertCount;
                vertices.AddVert(center + a * size, tint, Vector2.zero);
                vertices.AddVert(center + b * size, tint, Vector2.zero);
                vertices.AddVert(center + b * size * 0.68f, tint, Vector2.zero);
                vertices.AddVert(center + a * size * 0.68f, tint, Vector2.zero);
                vertices.AddTriangle(first, first + 1, first + 2);
                vertices.AddTriangle(first, first + 2, first + 3);
            }
        }
    }
}
