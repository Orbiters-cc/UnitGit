using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    internal enum UnitGitIconKind
    {
        ChevronCollapsed,
        ChevronExpanded,
        Close,
        CreateBranch,
        Update,
        Delete,
        Fetch,
        GitHub,
        PreviousDifference,
        NextDifference,
        Search
    }

    internal sealed class UnitGitIconElement : VisualElement
    {
        private readonly UnitGitIconKind kind;

        public UnitGitIconElement(UnitGitIconKind kind)
        {
            this.kind = kind;
            pickingMode = PickingMode.Ignore;
            generateVisualContent += DrawIcon;
        }

        private void DrawIcon(MeshGenerationContext context)
        {
            Rect rect = contentRect;
            if (rect.width <= 0f || rect.height <= 0f)
            {
                return;
            }

            var painter = context.painter2D;
            painter.fillColor = Color.white;
            painter.strokeColor = Color.white;
            painter.lineWidth = 0f;

            switch (kind)
            {
                case UnitGitIconKind.ChevronCollapsed:
                    DrawTriangle(painter, rect, 16f, 16f, new[]
                    {
                        new Vector2(6f, 3.5f),
                        new Vector2(11f, 8f),
                        new Vector2(6f, 12.5f)
                    });
                    break;
                case UnitGitIconKind.ChevronExpanded:
                    DrawTriangle(painter, rect, 16f, 16f, new[]
                    {
                        new Vector2(3.5f, 6f),
                        new Vector2(12.5f, 6f),
                        new Vector2(8f, 11f)
                    });
                    break;
                case UnitGitIconKind.Close:
                    DrawSegment(painter, rect, 16f, 16f, new Vector2(4.5f, 4.5f), new Vector2(11.5f, 11.5f), 2f);
                    DrawSegment(painter, rect, 16f, 16f, new Vector2(11.5f, 4.5f), new Vector2(4.5f, 11.5f), 2f);
                    break;
                case UnitGitIconKind.CreateBranch:
                    DrawCreateBranch(painter, rect);
                    break;
                case UnitGitIconKind.Update:
                    DrawUpdate(painter, rect);
                    break;
                case UnitGitIconKind.Delete:
                    DrawDelete(painter, rect);
                    break;
                case UnitGitIconKind.Fetch:
                    DrawFetch(painter, rect);
                    break;
                case UnitGitIconKind.GitHub:
                    DrawGitHub(painter, rect);
                    break;
                case UnitGitIconKind.PreviousDifference:
                    DrawTriangle(painter, rect, 16f, 16f, new[]
                    {
                        new Vector2(8f, 3f),
                        new Vector2(13f, 11f),
                        new Vector2(3f, 11f)
                    });
                    break;
                case UnitGitIconKind.NextDifference:
                    DrawTriangle(painter, rect, 16f, 16f, new[]
                    {
                        new Vector2(3f, 5f),
                        new Vector2(13f, 5f),
                        new Vector2(8f, 13f)
                    });
                    break;
                case UnitGitIconKind.Search:
                    DrawSearch(painter, rect);
                    break;
            }
        }

        private static void DrawCreateBranch(Painter2D painter, Rect rect)
        {
            DrawSegment(painter, rect, 24f, 24f, new Vector2(8f, 4f), new Vector2(8f, 20f), 2f);
            DrawSegment(painter, rect, 24f, 24f, new Vector2(8f, 12f), new Vector2(15f, 12f), 2f);
            DrawCircle(painter, rect, 24f, 24f, new Vector2(8f, 4f), 2.6f, 18);
            DrawCircle(painter, rect, 24f, 24f, new Vector2(8f, 20f), 2.6f, 18);
            DrawSegment(painter, rect, 24f, 24f, new Vector2(17f, 8f), new Vector2(17f, 16f), 2f);
            DrawSegment(painter, rect, 24f, 24f, new Vector2(13f, 12f), new Vector2(21f, 12f), 2f);
        }

        private static void DrawUpdate(Painter2D painter, Rect rect)
        {
            DrawArc(painter, rect, 24f, 24f, new Vector2(12f, 12f), 8f, 35f, 318f, 2f);
            DrawTriangle(painter, rect, 24f, 24f, new[]
            {
                new Vector2(5.1f, 18f),
                new Vector2(5.1f, 11.5f),
                new Vector2(11.4f, 18f)
            });
        }

        private static void DrawDelete(Painter2D painter, Rect rect)
        {
            DrawPolygon(painter, rect, 24f, 24f, new[]
            {
                new Vector2(3f, 4f),
                new Vector2(8f, 4f),
                new Vector2(8f, 3f),
                new Vector2(9f, 2f),
                new Vector2(15f, 2f),
                new Vector2(16f, 3f),
                new Vector2(16f, 4f),
                new Vector2(21f, 4f),
                new Vector2(22f, 5f),
                new Vector2(21f, 6f),
                new Vector2(3f, 6f),
                new Vector2(2f, 5f)
            });

            DrawPolygon(painter, rect, 24f, 24f, new[]
            {
                new Vector2(4f, 8f),
                new Vector2(20f, 8f),
                new Vector2(18.25f, 20.28f),
                new Vector2(17.48f, 21.55f),
                new Vector2(16.27f, 22f),
                new Vector2(7.74f, 22f),
                new Vector2(6.52f, 21.55f),
                new Vector2(5.76f, 20.28f)
            });
        }

        private static void DrawFetch(Painter2D painter, Rect rect)
        {
            DrawSegment(painter, rect, 24f, 24f, new Vector2(18f, 6f), new Vector2(7f, 17f), 2.2f);
            DrawSegment(painter, rect, 24f, 24f, new Vector2(7f, 17f), new Vector2(18f, 17f), 2.2f);
            DrawSegment(painter, rect, 24f, 24f, new Vector2(7f, 17f), new Vector2(7f, 6f), 2.2f);
        }

        private static void DrawGitHub(Painter2D painter, Rect rect)
        {
            DrawCircle(painter, rect, 20f, 20f, new Vector2(10f, 8.8f), 5.5f, 28);
            DrawTriangle(painter, rect, 20f, 20f, new[]
            {
                new Vector2(5.7f, 5.9f),
                new Vector2(5.2f, 2.8f),
                new Vector2(8.2f, 4.6f)
            });
            DrawTriangle(painter, rect, 20f, 20f, new[]
            {
                new Vector2(14.3f, 5.9f),
                new Vector2(14.8f, 2.8f),
                new Vector2(11.8f, 4.6f)
            });
            DrawBox(painter, rect, 20f, 20f, 7f, 13.2f, 6f, 3.4f);
            DrawSegment(painter, rect, 20f, 20f, new Vector2(8.1f, 15.7f), new Vector2(8.1f, 18.1f), 1.5f);
            DrawSegment(painter, rect, 20f, 20f, new Vector2(10f, 15.7f), new Vector2(10f, 18.2f), 1.5f);
            DrawSegment(painter, rect, 20f, 20f, new Vector2(11.9f, 15.7f), new Vector2(11.9f, 18.1f), 1.5f);
            DrawSegment(painter, rect, 20f, 20f, new Vector2(7.4f, 15.2f), new Vector2(5.6f, 14.2f), 1.3f);
        }

        private static void DrawSearch(Painter2D painter, Rect rect)
        {
            DrawArc(painter, rect, 16f, 16f, new Vector2(6.8f, 6.8f), 4.2f, 0f, 360f, 1.9f);
            DrawSegment(painter, rect, 16f, 16f, new Vector2(9.8f, 9.8f), new Vector2(13.1f, 13.1f), 2f);
        }

        private static void DrawArc(Painter2D painter, Rect rect, float viewWidth, float viewHeight, Vector2 center, float radius, float startDegrees, float endDegrees, float width)
        {
            const int segments = 24;
            Vector2 previous = PointOnCircle(center, radius, startDegrees);
            for (int i = 1; i <= segments; i++)
            {
                float angle = Mathf.Lerp(startDegrees, endDegrees, i / (float)segments);
                Vector2 current = PointOnCircle(center, radius, angle);
                DrawSegment(painter, rect, viewWidth, viewHeight, previous, current, width);
                previous = current;
            }
        }

        private static Vector2 PointOnCircle(Vector2 center, float radius, float degrees)
        {
            float radians = degrees * Mathf.Deg2Rad;
            return center + new Vector2(Mathf.Cos(radians), Mathf.Sin(radians)) * radius;
        }

        private static void DrawBox(Painter2D painter, Rect rect, float viewWidth, float viewHeight, float x, float y, float width, float height)
        {
            DrawPolygon(painter, rect, viewWidth, viewHeight, new[]
            {
                new Vector2(x, y),
                new Vector2(x + width, y),
                new Vector2(x + width, y + height),
                new Vector2(x, y + height)
            });
        }

        private static void DrawSegment(Painter2D painter, Rect rect, float viewWidth, float viewHeight, Vector2 a, Vector2 b, float width)
        {
            Vector2 direction = b - a;
            if (direction.sqrMagnitude <= Mathf.Epsilon)
            {
                DrawCircle(painter, rect, viewWidth, viewHeight, a, width * 0.5f, 12);
                return;
            }

            Vector2 normal = new Vector2(-direction.y, direction.x).normalized * (width * 0.5f);
            DrawPolygon(painter, rect, viewWidth, viewHeight, new[]
            {
                a + normal,
                b + normal,
                b - normal,
                a - normal
            });
        }

        private static void DrawTriangle(Painter2D painter, Rect rect, float viewWidth, float viewHeight, Vector2[] points)
        {
            DrawPolygon(painter, rect, viewWidth, viewHeight, points);
        }

        private static void DrawCircle(Painter2D painter, Rect rect, float viewWidth, float viewHeight, Vector2 center, float radius, int segments)
        {
            var points = new Vector2[segments];
            for (int i = 0; i < segments; i++)
            {
                float angle = i / (float)segments * Mathf.PI * 2f;
                points[i] = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
            }

            DrawPolygon(painter, rect, viewWidth, viewHeight, points);
        }

        private static void DrawPolygon(Painter2D painter, Rect rect, float viewWidth, float viewHeight, Vector2[] points)
        {
            float scale = Mathf.Min(rect.width / viewWidth, rect.height / viewHeight);
            float offsetX = rect.x + (rect.width - viewWidth * scale) * 0.5f;
            float offsetY = rect.y + (rect.height - viewHeight * scale) * 0.5f;

            painter.BeginPath();
            for (int i = 0; i < points.Length; i++)
            {
                Vector2 point = new Vector2(offsetX + points[i].x * scale, offsetY + points[i].y * scale);
                if (i == 0)
                {
                    painter.MoveTo(point);
                }
                else
                {
                    painter.LineTo(point);
                }
            }

            painter.ClosePath();
            painter.Fill(FillRule.NonZero);
        }
    }
}
