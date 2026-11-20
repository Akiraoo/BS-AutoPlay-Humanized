using UnityEngine;
using BeatLeader.Models.Replay;
using BeatLeader.Utils;
using Vector3 = UnityEngine.Vector3;
using BLNoteCutInfo = BeatLeader.Models.Replay.NoteCutInfo;

namespace BotPlay
{
    public static class NoteEventBuilder
    {
        /// <summary>照生成的軌跡算出來的切擊資料 (CutScorer) 寫事件; cut 為 null 時退回舊版的 115 分</summary>
        public static NoteEvent CreateNoteEvent(NoteData note, bool noArrows, CutScorer.Cut? cut)
        {
            var ev = Create115NoteEvent(note, noArrows);
            if (cut == null) return ev;
            var c = cut.Value;
            var ci = ev.noteCutInfo;
            ci.saberSpeed = c.speed;
            ci.saberDir = BotPlayMath.ToBLVector(c.saberDir);
            ci.cutPoint = BotPlayMath.ToBLVector(c.point);
            ci.cutNormal = BotPlayMath.ToBLVector(c.normal);
            ci.cutDistanceToCenter = c.dist;
            ci.cutDirDeviation = c.dirDev;
            ci.cutAngle = c.dirDev - 90f;
            ci.beforeCutRating = c.before;
            ci.afterCutRating = c.after;
            ci.timeDeviation = c.timeDev;
            ev.noteCutInfo = ci;
            ev.eventTime = note.time - c.timeDev;
            return ev;
        }

        public static NoteEvent Create115NoteEvent(NoteData note, bool noArrows = false)
        {
            Vector3 notePos = BotPlayMath.GetNotePos(note);

            Vector3 cutDir = Vector3.down;
            Vector3 cutNormal = Vector3.right;

            switch (note.cutDirection)
            {
                case NoteCutDirection.Down: cutDir = Vector3.down; cutNormal = Vector3.right; break;
                case NoteCutDirection.Up: cutDir = Vector3.up; cutNormal = Vector3.left; break;
                case NoteCutDirection.Left: cutDir = Vector3.left; cutNormal = Vector3.up; break;
                case NoteCutDirection.Right: cutDir = Vector3.right; cutNormal = Vector3.down; break;
                case NoteCutDirection.UpLeft: cutDir = new Vector3(-0.7071f, 0.7071f, 0f); cutNormal = new Vector3(0.7071f, 0.7071f, 0f); break;
                case NoteCutDirection.UpRight: cutDir = new Vector3(0.7071f, 0.7071f, 0f); cutNormal = new Vector3(0.7071f, -0.7071f, 0f); break;
                case NoteCutDirection.DownLeft: cutDir = new Vector3(-0.7071f, -0.7071f, 0f); cutNormal = new Vector3(-0.7071f, 0.7071f, 0f); break;
                case NoteCutDirection.DownRight: cutDir = new Vector3(0.7071f, -0.7071f, 0f); cutNormal = new Vector3(-0.7071f, -0.7071f, 0f); break;
                default: cutDir = Vector3.down; cutNormal = Vector3.right; break;
            }

            var cutInfo = new BLNoteCutInfo
            {
                speedOK = true,
                directionOK = true,
                saberTypeOK = true,
                wasCutTooSoon = false,
                saberSpeed = 42.0f,
                cutDistanceToCenterPositive = true,
                cutDistanceToCenter = 0.0f,
                cutDirDeviation = 0.0f,
                beforeCutRating = 1.0f,
                afterCutRating = 1.0f,
                saberType = (int)note.colorType,
                timeDeviation = 0.0f,
                cutAngle = 0.0f,
                cutPoint = BotPlayMath.ToBLVector(notePos),
                cutNormal = BotPlayMath.ToBLVector(cutNormal),
                saberDir = BotPlayMath.ToBLVector(cutDir)
            };

            int noteId = ReplayDataUtils.ComputeNoteId(note, false, false, false);
            // NA(無箭頭)下, 回放器裡的 NoteData 切割方向已經被遊戲改成 Any(8),
            // 而 BeatLeader 是靠"時間 + 音符 ID"去匹配事件的, ID 末位就是切割方向.
            // 這裡的 ID 如果還是原譜的方向, 就會匹配不上 -> 顯示成 miss. 所以 NA 時把方向替換成 Any.
            if (noArrows)
                noteId = noteId - (int)note.cutDirection + (int)NoteCutDirection.Any;
            return new NoteEvent
            {
                noteID = noteId,
                spawnTime = note.time,
                eventTime = note.time,
                eventType = NoteEventType.good,
                noteCutInfo = cutInfo
            };
        }
    }
}


