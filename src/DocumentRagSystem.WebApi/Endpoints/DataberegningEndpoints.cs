namespace DocumentRagSystem.WebApi.Endpoints;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

public static class DataberegningEndpoints
{
    public static void MapDataberegning(this IEndpointRouteBuilder app)
    {
        app.MapGet("/databeregningsform", () => Results.Content("""
            <!doctype html>
            <html>
            <head>
            <meta charset="utf-8">
            <title>EBM-Pabst databeregning form</title>
            <style>
                html { scrollbar-gutter: stable; }
                body { margin: 0; min-height: 100vh; display: flex; justify-content: center; background: #f8fafc; color: #1e293b; font-family: system-ui, sans-serif; }
                aside { position: fixed; top: 0; left: 0; width: 260px; height: 100vh; box-sizing: border-box; overflow-y: auto; padding: 24px 16px; background: #ffffff; border-right: 1px solid #e2e8f0; }
                aside h2 { margin: 0; font-size: 16px; color: #0284c7; }
                .ny-top { width: 100%; margin-bottom: 20px; }
                .historik-top { display: flex; justify-content: space-between; align-items: center; margin-bottom: 16px; }
                #sync { padding: 4px 10px; }
                #sync:disabled { opacity: .5; cursor: wait; }
                #historik { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 8px; }
                .tom { color: #64748b; font-size: 14px; }
                .historik-punkt { width: 100%; display: flex; flex-direction: column; align-items: flex-start; gap: 2px; padding: 8px 10px; text-align: left; }
                .historik-punkt small { color: #64748b; font-size: 12px; }
                .historik-punkt.aktiv { background: #ffffff; border-color: #f59e0b; }
                #form { display: flex; flex-direction: column; align-items: center; gap: 20px; padding: 40px 16px; }
                h1 { margin: 0; color: #0f172a; }
                #stamdata { display: grid; grid-template-columns: 1fr 1fr; gap: 12px 16px; width: 618px; }
                #stamdata label { display: flex; flex-direction: column; gap: 4px; font-size: 13px; color: #0284c7; }
                #banner { display: none; align-items: center; gap: 12px; padding: 10px 16px; border: 1px solid #f59e0b; border-radius: 8px; background: #1f1503; color: #fcd34d; font-size: 14px; }
                #banner small { color: #b4914a; }
                #ventilatorer { display: flex; flex-direction: column; gap: 16px; }
                .ventilator { position: relative; background: #ffffff; border: 1px solid #cbd5e1; border-radius: 12px; padding: 20px; }
                .ventilator summary { cursor: pointer; font-size: 18px; font-weight: 600; color: #0284c7; }
                .ventilator[open] summary { margin-bottom: 12px; }
                .ventilator:not([open]) { width: 576px; }
                .ventilator:only-child .slet { opacity: .4; cursor: not-allowed; }
                .ventilator:only-child .slet:hover { border-color: #cbd5e1; color: #e6ecff; }
                .felter { display: grid; grid-auto-flow: column; grid-template-rows: repeat(11, auto); grid-auto-columns: 280px; gap: 8px 16px; }
                .felter h3 { margin: 0; font-size: 13px; text-transform: uppercase; letter-spacing: .08em; color: #0284c7; }
                .felt { position: relative; display: flex; }
                .felt input { flex: 1; min-width: 0; }
                .felt:has(.enhed) input { padding-right: 54px; }
                .enhed { position: absolute; right: 6px; top: 50%; transform: translateY(-50%); padding: 2px 6px; border-radius: 4px; background: #ffffff; color: #0284c7; font-size: 11px; pointer-events: none; }
                .dele { display: flex; gap: 6px; }
                .dele .felt { flex: 1; min-width: 0; }
                .dele input { font-size: 12px; padding-left: 6px; }
                .dele .felt:has(.enhed) input { padding-right: 26px; }
                .dele .enhed { right: 4px; padding: 1px 4px; }
                input { padding: 8px 10px; background: #ffffff; color: #0f172a; border: 1px solid #cbd5e1; border-radius: 6px; }
                input:focus { outline: none; border-color: #3b82f6; }
                input:read-only { background: #f1f5f9; }
                .knapper { display: flex; gap: 8px; }
                button { padding: 10px 18px; border-radius: 8px; border: 1px solid #1e40af; background: #ffffff; color: #0f172a; cursor: pointer; }
                button:hover { border-color: #3b82f6; }
                .gem { background: #0284c7; color: #ffffff !important; border-color: #0284c7; font-weight: 500; } .gem:hover { background: #0369a1; color: #ffffff !important; border-color: #0369a1; }
                .slet { position: absolute; top: 16px; right: 16px; padding: 4px 10px; font-size: 13px; }
                .slet:hover { border-color: #ef4444; color: #fca5a5; }
                .vis-knapper { display: none; }
                .laast #banner { display: flex; }
                .laast .ventilator { background: #f8fafc; border-color: #cbd5e1; }
                .laast input { background: #f1f5f9; color: #64748b; border: 1px dashed #cbd5e1; cursor: default; }
                .laast input:focus { border-color: #cbd5e1; }
                .laast .slet, .laast .ny-knapper { display: none; }
                .laast .vis-knapper { display: flex; }
                .ryd { border-color: #7f1d1d; color: #fca5a5; }
                .ryd:hover { border-color: #ef4444; background: #450a0a; color: #fecaca; }
            </style>
            </head>
            <body>
            <aside>
              <button type="button" class="gem ny-top" onclick="nyBeregning()">+ Ny beregning</button>
              <div class="historik-top">
                <h2>Tidligere beregninger</h2>
                <button type="button" id="sync" onclick="hentHistorik()">↻</button>
              </div>
              <ul id="historik">
                <li class="tom">Ingen beregninger endnu</li>
              </ul>
            </aside>

            <main>
              <form id="form">
                <div id="banner">🔒 Tidligere beregning – kan ikke ændres <small id="banner-dato"></small></div>
                <h1 id="overskrift">EBM-Pabst databeregning</h1>
                <div id="stamdata">
                    <label>Installeringssted<input name="installeringssted" placeholder="Installatørens kunde" required></label>
                    <label>Kunde<input name="kunde" placeholder="Installatørens selskab" required></label>
                    <label>Kundes adresse<input name="kundesAdresse" placeholder="Installatørens adresse" required></label>
                    <label>Kontakt<input name="kontakt" placeholder="Installatørens medarbejder" required></label>
                </div>
                <div id="ventilatorer"></div>
                <div class="knapper ny-knapper">
                  <button type="button" onclick="tilfoejVentilator()">Tilføj ventilator</button>
                  <button type="button" class="ryd" onclick="nyBeregning()">Ryd felter</button>
                  <button class="gem">Gem</button>
                </div>
                <div class="knapper vis-knapper">
                  <button type="button" class="gem" onclick="nyBeregning()">Ny beregning</button>
                </div>
              </form>
            </main>

            <script>
              const felter = [
                ["ventilatorIdInd", "Ventilator-ID", ""],
                ["luftmaengdeMaaltInd", "Luftmængde målt", "m³/h"],
                ["luftmaengdeMaxInd", "Evt. Luftmængde max", "m³/h"],
                ["statiskTrykInd", "Statisk tryk over ventilator", "Pa"],
                ["totalTrykInd", "Total tryk over ventilator", "Pa"],
                [["kammermaalHInd", "H", "mm"], ["kammermaalDInd", "D", "mm"], ["kammermaalLInd", "L", "mm"]],
                ["aarligDriftstidInd", "Årlig driftstid", "timer"],
                ["statiskVirkningsgradInd", "Statisk virknings-grad", "%"],
                [["stroemInd", "Strøm", "A"], ["spaendingInd", "Spænding", "V"], ["cosInd", "COS", "d"]],
                ["optagetEffektInd", "Optaget effekt", "kW"],
                ["forbrugAarligtInd", "Forbrug årligt", "kWh"],

                ["ventilatorIdUd", "Ventilator-ID", ""],
                ["luftmaengdeMaaltUd", "Luftmængde målt", "m³/h"],
                ["luftmaengdeMaxUd", "Evt. Luftmængde max", "m³/h"],
                ["statiskTrykUd", "Statisk tryk over ventilator", "Pa"],
                ["totalTrykUd", "Total tryk over ventilator", "Pa"],
                [["kammermaalHUd", "H", "mm"], ["kammermaalDUd", "D", "mm"], ["kammermaalLUd", "L", "mm"]],
                ["aarligDriftstidUd", "Årlig driftstid", "timer"],
                ["statiskVirkningsgradUd", "Statisk virknings-grad", "%"],
                [["stroemUd", "Strøm", "A"], ["spaendingUd", "Spænding", "V"], ["cosUd", "COS", "d"]],
                ["optagetEffektUd", "Optaget effekt", "kW"],
                ["forbrugAarligtUd", "Forbrug årligt", "kWh"]
              ];

              const valgfri = /^(luftmaengdeMax|stroem|spaending|cos|statiskTryk|totalTryk)/;
              const beregnet = /^(statiskVirkningsgrad|forbrugAarligt|ventilatorId)/;

              let beregninger = [];
              const form = document.getElementById("form");
              form.addEventListener("invalid", e => e.target.closest("details")?.setAttribute("open", ""), true);
              const stamInputs = () => document.querySelectorAll("#stamdata input");

              const felt = (name, label, enhed) => skjult.test(name) ? `<input type="hidden" name="${name}">` : `<label class="felt">${enhed ? `<span class="enhed">${enhed}</span>` : ""}<input name="${name}" placeholder="${label}" title="${label}" ${valgfri.test(name) ? "" : "required"} ${beregnet.test(name) ? "readonly" : ""}></label>`;

              const inputs = f => f.map(e => Array.isArray(e[0])
                ? `<div class="dele">${e.map(([n, l, en]) => felt(n, l, en)).join("")}</div>`
                : felt(...e)).join("");

              function nummerer() {
                document.querySelectorAll(".ventilator").forEach((v, i) => {
                  const nr = String(i + 1).padStart(2, "0");
                  v.id = `ventilator${i + 1}`;
                  v.querySelector(".titel").textContent = `Ventilator ${i + 1}`;
                  v.querySelector("[name=ventilatorIdInd]").value = `VE${nr} ind`;
                  v.querySelector("[name=ventilatorIdUd]").value = `VE${nr} ud`;
                });
              }

              function tilfoejVentilator() {
                const ventilator = document.createElement("details");
                ventilator.open = true;
                ventilator.className = "ventilator";
                ventilator.innerHTML = `<summary><span class="titel"></span><button type="button" class="slet" onclick="sletVentilator(this)">Slet</button></summary>
                  <div class="felter"><h3>Ind</h3>${inputs(felter.slice(0, 11))}<h3>Ud</h3>${inputs(felter.slice(11))}</div>`;
                document.getElementById("ventilatorer").append(ventilator);
                nummerer();
                return ventilator;
              }

              function sletVentilator(knap) {
                if (document.querySelectorAll(".ventilator").length === 1) return;
                knap.closest(".ventilator").remove();
                nummerer();
                gemKladde();
              }

              async function hentHistorik() {
                const sync = document.getElementById("sync");
                sync.disabled = true;
                beregninger = await (await fetch("/databeregning")).json();
                document.getElementById("historik").innerHTML = beregninger.length
                  ? beregninger.map((b, i) => `<li><button type="button" class="historik-punkt" onclick="aabnBeregning(${i})">${b.titel}<small>${b.createdAt}</small></button></li>`).join("")
                  : `<li class="tom">Ingen beregninger endnu</li>`;
                sync.disabled = false;
              }

              function markerAktiv(i) {
                document.querySelectorAll(".historik-punkt").forEach((k, j) => k.classList.toggle("aktiv", j === i));
              }

              const hentData = () => ({
                ...Object.fromEntries([...stamInputs()].map(i => [i.name, i.value])),
                ventilatorer: [...document.querySelectorAll(".ventilator")].flatMap(k => ["Ind", "Ud"].map(s =>
                  Object.fromEntries([...k.querySelectorAll(`input[name$=${s}]`)].map(i => [i.name.slice(0, -s.length), i.value]))))
              });

              const gemKladde = () => { if (!form.classList.contains("laast")) localStorage.setItem("kladde", JSON.stringify(hentData())); };
              const hentKladde = () => JSON.parse(localStorage.getItem("kladde"));
              const skjult = /^ventilatorId/;

              const tal = v => parseFloat(v.replace(/\./g, "").replace(",", "."));

              const virkningsgrad = felt => {
                const statisk = felt("statiskTryk"), total = felt("totalTryk");
                total.readOnly = statisk.value !== "";
                statisk.readOnly = total.value !== "";
                const q = tal(felt("luftmaengdeMaalt").value), p = tal(statisk.value || total.value), e = tal(felt("optagetEffekt").value);
                felt("statiskVirkningsgrad").value = q && p && e ? ((q / 3600 * p) / (e * 1000) * 100).toFixed(1).replace(".", ",") : "";
              };

              const forbrug = felt => {
                const e = tal(felt("optagetEffekt").value), t = tal(felt("aarligDriftstid").value);
                felt("forbrugAarligt").value = e && t ? String(Math.round(e * t)) : "";
              };

              const beregn = ventilator => ["Ind", "Ud"].forEach(s => {
                const felt = n => ventilator.querySelector(`[name=${n}${s}]`);
                virkningsgrad(felt);
                forbrug(felt);
              });

              form.addEventListener("input", e => {
                const v = e.target.closest(".ventilator");
                if (v) beregn(v);
              });

              form.addEventListener("input", gemKladde);

              function udfyld(data, laast) {
                form.classList.toggle("laast", laast);
                stamInputs().forEach(input => {
                  input.value = data?.[input.name] ?? "";
                  input.readOnly = laast;
                });
                document.getElementById("ventilatorer").innerHTML = "";
                const v = data?.ventilatorer ?? [{}, {}];
                for (let i = 0; i < v.length; i += 2)
                  tilfoejVentilator().querySelectorAll("input").forEach(input => {
                    const [, n, s] = input.name.match(/(.*)(Ind|Ud)$/);
                    input.value = v[s === "Ind" ? i : i + 1]?.[n] ?? input.value;
                    input.readOnly = laast || beregnet.test(input.name);
                  });
                if (!laast) document.querySelectorAll(".ventilator").forEach(beregn);
              }

              function aabnBeregning(i) {
                gemKladde();
                const b = beregninger[i];
                udfyld(b.beregning, true);
                document.getElementById("overskrift").textContent = b.titel;
                document.getElementById("banner-dato").textContent = b.createdAt;
                markerAktiv(i);
              }

              function nyBeregning() {
                if (!form.classList.contains("laast")) localStorage.removeItem("kladde");
                udfyld(hentKladde(), false);
                document.getElementById("overskrift").textContent = "EBM-Pabst databeregning";
                markerAktiv(-1);
              }

              udfyld(hentKladde(), false);
              hentHistorik();

              form.onsubmit = async e => {
                e.preventDefault();
                if (form.classList.contains("laast")) return;
                await fetch("/databeregning", {
                  method: "POST",
                  headers: { "Content-Type": "application/json" },
                  body: JSON.stringify(hentData())
                });
                localStorage.removeItem("kladde");
                hentHistorik();
                nyBeregning();
              };
            </script>
            </body>
            </html>
            """, "text/html; charset=utf-8"))
        .RequireAuthorization()
        .WithName("databeregningsform");

        app.MapGet("/databeregning", async (IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            var user = config["Workqueue:User"];
            var pass = config["Workqueue:Password"];

            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            {
                return Results.Problem(
                    detail: "Workqueue legitimationsoplysninger er ikke konfigureret i miljøvariabler (WORKQUEUE_USER / WORKQUEUE_PASSWORD) eller user-secrets.",
                    title: "Workqueue ikke konfigureret",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var client = httpFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));

            try
            {
                var response = await client.GetAsync("https://backend.ecit-automate.com/crm/rest/v2/workqueue?ROBOT=287700");
                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    return Results.Content(errorBody, "application/json", statusCode: (int)response.StatusCode);
                }

                var items = await response.Content.ReadFromJsonAsync<List<WorkqueueItem>>();
                var result = items?.Select(i => new
                {
                    i.DataID,
                    i.TITEL,
                    CreatedAt = DateTime.TryParse(i.CreatedAt, CultureInfo.InvariantCulture, out var dt)
                        ? dt.ToString("dd-MM-yyyy HH:mm:ss")
                        : i.CreatedAt,
                    Beregning = string.IsNullOrWhiteSpace(i.INPUT) ? null : JsonSerializer.Deserialize<Databeregning>(i.INPUT)
                }) ?? Enumerable.Empty<object>();

                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: ex.Message, title: "Fejl ved hentning af databeregninger");
            }
        })
        .RequireAuthorization()
        .WithName("GetDataberegning");

        app.MapPost("/databeregning", async (Databeregning input, IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            var user = config["Workqueue:User"];
            var pass = config["Workqueue:Password"];

            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            {
                return Results.Problem(
                    detail: "Workqueue legitimationsoplysninger er ikke konfigureret i miljøvariabler (WORKQUEUE_USER / WORKQUEUE_PASSWORD) eller user-secrets.",
                    title: "Workqueue ikke konfigureret",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var client = httpFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));

            var d = DateTime.Now;

            var item = new Dictionary<string, string>
            {
                ["TITEL"] = $"{input.Installeringssted} - {d.ToString("d. MMMM yyyy", new CultureInfo("da-DK"))}",
                ["ROBOT"] = "287700",
                ["KUNDE"] = "112100",
                ["INPUT"] = JsonSerializer.Serialize(input),
                ["STATUSCHANGER"] = "142",
                ["StatusID"] = "In que"
            };

            try
            {
                var svar = await client.PostAsJsonAsync("https://backend.ecit-automate.com/crm/rest/v2/workqueue", item, new JsonSerializerOptions());
                var content = await svar.Content.ReadAsStringAsync();
                return Results.Content(content, "application/json", statusCode: (int)svar.StatusCode);
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: ex.Message, title: "Fejl ved oprettelse af databeregning");
            }
        })
        .RequireAuthorization()
        .WithName("PostDataberegning");
    }
}

public record WorkqueueItem(int DataID, string TITEL, string ROBOT, string CreatedAt, string INPUT);

public record Databeregning(string Installeringssted, string Kunde, string KundesAdresse, string Kontakt, List<DataberegningInput> Ventilatorer);

public record DataberegningInput(
    string VentilatorId,
    string LuftmaengdeMaalt,
    string LuftmaengdeMax,
    string StatiskTryk,
    string TotalTryk,
    string KammermaalH,
    string KammermaalD,
    string KammermaalL,
    string AarligDriftstid,
    string StatiskVirkningsgrad,
    string Stroem,
    string Spaending,
    string Cos,
    string OptagetEffekt,
    string ForbrugAarligt);
