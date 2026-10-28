using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using ParkShift.Core;

namespace ParkShift.UI
{
    public class ResultsScreen : MonoBehaviour
    {
        GameObject _panel; Text _body;

        public void Build(GameObject canvas)
        {
            _panel = new GameObject("Results", typeof(Image));
            _panel.transform.SetParent(canvas.transform, false);
            var img = _panel.GetComponent<Image>();
            img.color = new Color(0.05f, 0.05f, 0.12f, 0.94f);
            var rt = _panel.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.25f, 0.15f); rt.anchorMax = new Vector2(0.75f, 0.85f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;

            var txt = new GameObject("Body", typeof(Text));
            txt.transform.SetParent(_panel.transform, false);
            _body = txt.GetComponent<Text>();
            _body.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _body.fontSize = 26; _body.alignment = TextAnchor.UpperCenter; _body.color = Color.white;
            ComicUI.Letter(_body);
            var brt = _body.rectTransform;
            brt.anchorMin = new Vector2(0, 0.18f); brt.anchorMax = Vector2.one;
            brt.offsetMin = new Vector2(20, 0); brt.offsetMax = new Vector2(-20, -20);

            var btnGO = new GameObject("Restart", typeof(Image), typeof(Button));
            btnGO.transform.SetParent(_panel.transform, false);
            var bb = btnGO.GetComponent<RectTransform>();
            bb.anchorMin = new Vector2(0.3f, 0.04f); bb.anchorMax = new Vector2(0.7f, 0.14f);
            bb.offsetMin = bb.offsetMax = Vector2.zero;
            btnGO.GetComponent<Image>().color = ComicUI.Gold;
            var bt = new GameObject("L", typeof(Text)); bt.transform.SetParent(btnGO.transform, false);
            var btx = bt.GetComponent<Text>();
            btx.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            btx.text = Networking.NetworkBootstrap.Mode == Networking.NetMode.Solo ? "NEXT SHIFT" : "LEAVE SESSION"; btx.alignment = TextAnchor.MiddleCenter; btx.fontSize = 24; btx.color = Color.black;
            var btr = btx.rectTransform; btr.anchorMin = Vector2.zero; btr.anchorMax = Vector2.one; btr.offsetMin = btr.offsetMax = Vector2.zero;
            btnGO.GetComponent<Button>().onClick.AddListener(() =>
            {
                if (Networking.NetworkBootstrap.Mode == Networking.NetMode.Solo)
                    SceneManager.LoadScene(SceneManager.GetActiveScene().name);
                else Networking.NetworkBootstrap.LeaveSession();
            });

            _panel.SetActive(false);
        }

        public void Show(AttractionSim sim)
        {
            int score = sim.Score.Compute(sim.Guests.Satisfaction);
            string grade = sim.Score.Grade(score);
            // Grade-themed fanfare (triumphant for S/A, glum for D/F). Null-safe if no director.
            ParkShift.Juice.AudioDirector.Fanfare(grade.Length > 0 ? grade[0] : 'C');
            _body.text =
                "SHIFT COMPLETE — COSMIC CONVEYOR\n\n" +
                $"Riders served: {sim.Score.RidersServed}\n" +
                $"Dispatches: {sim.Score.Dispatches}\n" +
                $"Faults resolved: {sim.Score.FaultsResolved}\n" +
                $"Best dispatch streak: x{sim.Score.BestStreak}\n" +
                $"Combo bonus: +{sim.Score.ComboBonusPoints}\n" +
                $"Downtime: {sim.Score.DowntimeSeconds:0.0}s\n" +
                $"Final satisfaction: {sim.Guests.Satisfaction:0}%\n\n" +
                $"SCORE: {score}\n{grade}";
            _panel.SetActive(true);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            var role = ParkShift.Player.RoleController.Local;
            var fps = role != null ? role.GetComponent<ParkShift.Player.FirstPersonController>() : null;
            if (fps != null) fps.InputEnabled = false;
        }
    }
}



