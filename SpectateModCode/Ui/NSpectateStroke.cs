using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Helpers;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// One line drawn on a teammate's screen. Every point remembers when it was laid down and fades out after a while, so
/// the line wipes itself away from its oldest end, following the pen, and frees itself once nothing is left.
/// </summary>
public partial class NSpectateStroke : Line2D
{
    /// <summary>How long a point stays at full strength.</summary>
    private const float holdSeconds = 1.5f;

    /// <summary>How long a point then takes to fade out completely.</summary>
    private const float fadeSeconds = 1.5f;

    /// <summary>Same as the map's: closer points than this add nothing but cost.</summary>
    private const float minPointDistance = 2f;

    private const float lineWidth = 6f;

    private readonly List<double> _pointTimes = new();
    private readonly Gradient _fade = new();
    private Color _color;
    private bool _ended;

    public static NSpectateStroke Create(Vector2 start, Color color)
    {
        NSpectateStroke stroke = new()
        {
            Name = "SpectateStroke",
            Width = lineWidth,
            BeginCapMode = LineCapMode.Round,
            EndCapMode = LineCapMode.Round,
            JointMode = LineJointMode.Round,
            Antialiased = true,
            _color = color
        };
        stroke.Gradient = stroke._fade;
        // Two points from the start, like the map does, so a click that never moves still leaves a dot.
        stroke.Append(start);
        stroke.Append(start + new Vector2(0f, 0.5f));
        stroke.RefreshFade(Now());
        return stroke;
    }

    public void Extend(Vector2 position)
    {
        if (_ended)
        {
            return;
        }
        if (GetPointCount() > 0 && GetPointPosition(GetPointCount() - 1).DistanceSquaredTo(position)
            < minPointDistance * minPointDistance)
        {
            return;
        }
        Append(position);
    }

    /// <summary>The pen has lifted. The line stays until it has faded; it just takes no more points.</summary>
    public void End()
    {
        _ended = true;
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        double now = Now();
        while (_pointTimes.Count > 0 && now - _pointTimes[0] >= holdSeconds + fadeSeconds)
        {
            _pointTimes.RemoveAt(0);
            RemovePoint(0);
        }
        if (_pointTimes.Count == 0)
        {
            if (_ended)
            {
                this.QueueFreeSafely();
            }
            return;
        }
        RefreshFade(now);
    }

    private void Append(Vector2 position)
    {
        AddPoint(position);
        _pointTimes.Add(Now());
    }

    /// <summary>
    /// A gradient runs along the line's length, so fading the oldest end to its points' age and the newest end to
    /// theirs gives the tail its taper.
    /// </summary>
    private void RefreshFade(double now)
    {
        if (_pointTimes.Count == 0)
        {
            return;
        }
        _fade.SetColor(0, _color with { A = _color.A * AlphaAt(now - _pointTimes[0]) });
        _fade.SetColor(1, _color with { A = _color.A * AlphaAt(now - _pointTimes[^1]) });
    }

    private static float AlphaAt(double age)
    {
        return 1f - Mathf.Clamp((float)((age - holdSeconds) / fadeSeconds), 0f, 1f);
    }

    private static double Now()
    {
        return Time.GetTicksMsec() / 1000.0;
    }
}
