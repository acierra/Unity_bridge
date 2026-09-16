using UnityEngine;

[RequireComponent(typeof(Renderer))]
public class CsharpTextureSmokeTest : MonoBehaviour
{
    [SerializeField] private int m_Width = 512;
    [SerializeField] private int m_Height = 288;
    [SerializeField] private float m_ColorCycleSpeed = 0.4f;
    [SerializeField] private float m_GradientSpeed = 0.35f;
    [SerializeField] private int m_DigitScale = 6;

    private static readonly byte[][] s_Digits =
    {
        new byte[] { 1, 1, 1, 1, 0, 1, 1, 0, 1, 1, 0, 1, 1, 1, 1 },
        new byte[] { 0, 1, 0, 1, 1, 0, 0, 1, 0, 0, 1, 0, 1, 1, 1 },
        new byte[] { 1, 1, 1, 0, 0, 1, 1, 1, 1, 1, 0, 0, 1, 1, 1 },
        new byte[] { 1, 1, 1, 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 1, 1 },
        new byte[] { 1, 0, 1, 1, 0, 1, 1, 1, 1, 0, 0, 1, 0, 0, 1 },
        new byte[] { 1, 1, 1, 1, 0, 0, 1, 1, 1, 0, 0, 1, 1, 1, 1 },
        new byte[] { 1, 1, 1, 1, 0, 0, 1, 1, 1, 1, 0, 1, 1, 1, 1 },
        new byte[] { 1, 1, 1, 0, 0, 1, 0, 1, 0, 1, 0, 0, 1, 0, 0 },
        new byte[] { 1, 1, 1, 1, 0, 1, 1, 1, 1, 1, 0, 1, 1, 1, 1 },
        new byte[] { 1, 1, 1, 1, 0, 1, 1, 1, 1, 0, 0, 1, 1, 1, 1 },
    };

    private Renderer m_Renderer;
    private Texture2D m_Texture;
    private Color32[] m_Pixels;
    private Material m_RuntimeMaterial;
    private int m_FrameCount;

    private void Start()
    {
        m_Renderer = GetComponent<Renderer>();

        int width = Mathf.Max(64, m_Width);
        int height = Mathf.Max(64, m_Height);

        m_Texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        m_Texture.wrapMode = TextureWrapMode.Clamp;
        m_Texture.filterMode = FilterMode.Bilinear;
        m_Pixels = new Color32[width * height];

        Shader shader = Shader.Find("Unlit/Texture");
        if (shader == null)
        {
            shader = Shader.Find("Standard");
        }

        m_RuntimeMaterial = new Material(shader);
        m_RuntimeMaterial.mainTexture = m_Texture;
        m_Renderer.material = m_RuntimeMaterial;
    }

    private void Update()
    {
        if (m_Texture == null || m_Pixels == null)
        {
            return;
        }

        m_FrameCount++;

        float t = Time.time;
        int width = m_Texture.width;
        int height = m_Texture.height;

        for (int y = 0; y < height; y++)
        {
            float yf = (float)y / Mathf.Max(1, height - 1);

            for (int x = 0; x < width; x++)
            {
                float xf = (float)x / Mathf.Max(1, width - 1);
                float wave = 0.5f + 0.5f * Mathf.Sin((xf * 5.0f) + (yf * 3.0f) + (t * m_GradientSpeed * 4.0f));

                byte r = ToByte(0.5f + 0.5f * Mathf.Sin((xf * 4.0f) + (t * m_ColorCycleSpeed)));
                byte g = ToByte(0.5f + 0.5f * Mathf.Sin((yf * 6.0f) + (t * (m_ColorCycleSpeed + 0.4f))));
                byte b = ToByte(wave);

                m_Pixels[(y * width) + x] = new Color32(r, g, b, 255);
            }
        }

        DrawFrameCounter(m_FrameCount, 18, 18, m_DigitScale, new Color32(0, 0, 0, 255));
        DrawFrameCounter(m_FrameCount, 16, 16, m_DigitScale, new Color32(255, 255, 255, 255));
        DrawTimeBar(t);

        m_Texture.SetPixels32(m_Pixels);
        m_Texture.Apply(false, false);
    }

    private void OnDestroy()
    {
        if (m_Texture != null)
        {
            Destroy(m_Texture);
        }

        if (m_RuntimeMaterial != null)
        {
            Destroy(m_RuntimeMaterial);
        }
    }

    private void DrawFrameCounter(int value, int startX, int startY, int scale, Color32 color)
    {
        string text = value.ToString();
        int cursor = startX;

        for (int i = 0; i < text.Length; i++)
        {
            int digit = text[i] - '0';
            DrawDigit(digit, cursor, startY, scale, color);
            cursor += (4 * scale);
        }
    }

    private void DrawDigit(int digit, int startX, int startY, int scale, Color32 color)
    {
        if (digit < 0 || digit > 9)
        {
            return;
        }

        byte[] glyph = s_Digits[digit];
        for (int row = 0; row < 5; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                if (glyph[(row * 3) + col] == 0)
                {
                    continue;
                }

                FillRect(startX + (col * scale), startY + ((4 - row) * scale), scale, scale, color);
            }
        }
    }

    private void DrawTimeBar(float t)
    {
        int width = m_Texture.width;
        int barWidth = Mathf.RoundToInt((0.15f + (0.75f * (0.5f + (0.5f * Mathf.Sin(t))))) * width);
        FillRect(0, 0, barWidth, 12, new Color32(255, 210, 64, 255));
    }

    private void FillRect(int startX, int startY, int width, int height, Color32 color)
    {
        int textureWidth = m_Texture.width;
        int textureHeight = m_Texture.height;

        int endX = Mathf.Min(textureWidth, startX + width);
        int endY = Mathf.Min(textureHeight, startY + height);

        for (int y = Mathf.Max(0, startY); y < endY; y++)
        {
            int row = y * textureWidth;
            for (int x = Mathf.Max(0, startX); x < endX; x++)
            {
                m_Pixels[row + x] = color;
            }
        }
    }

    private static byte ToByte(float value)
    {
        return (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255.0f), 0, 255);
    }
}
