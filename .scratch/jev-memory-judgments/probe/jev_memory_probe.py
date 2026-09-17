#!/usr/bin/env python3
"""Probe Jev on the three memory judgments: A gate, B candidate verification, C pairwise relation.

Synthetic data only. Key: $TYPESAFE_API_KEY, else ~/.config/typesafe/key.
"""
import http.client, json, os, pathlib, statistics, sys, time

MODEL = os.environ.get("JEV_MODEL", "jev-1.13.0")


def key():
    k = os.environ.get("TYPESAFE_API_KEY")
    if k:
        return k.strip()
    p = pathlib.Path.home() / ".config/typesafe/key"
    if p.exists():
        return p.read_text().strip()
    sys.exit("no key")


CONN = http.client.HTTPSConnection("api.typesafe.ai", timeout=30)
LAT = []


def ask(state, questions, k):
    body = json.dumps({"state": state, "model": MODEL, "questions": questions})
    t = time.perf_counter()
    CONN.request("POST", "/v1/systemone", body, {"Authorization": f"Bearer {k}", "Content-Type": "application/json"})
    r = CONN.getresponse()
    d = json.loads(r.read())
    LAT.append((time.perf_counter() - t) * 1000)
    if "answers" not in d:
        sys.exit(f"API error: {d}")
    return d


# ---------- A: gate. (context, current, holds_something_durable)
A_CASES = [
    ([], "hola, ¿qué tal?", False),
    ([], "gracias", False),
    ([], "enciende la luz del salón", False),
    ([], "¿qué tiempo hace mañana en Valencia?", False),
    ([], "pon un temporizador de ocho minutos", False),
    ([], "busca cuánto tiene que reposar el gazpacho", False),
    ([], "how do I switch the CoT role?", False),
    ([], "is the complete series purchasable in English on Amazon.es?", False),
    ([], "ayer llegué tarde a casa, estoy reventado", False),
    ([], "estoy intentando arreglar el script este, no me sale", False),
    ([("user", "pon música"), ("assistant", "¿Qué te apetece?")], "algo tranquilo", False),
    ([("assistant", "¿Apago también la del pasillo?")], "sí", False),
    ([], "trabajo en Globex como ingeniero de datos", True),
    ([], "a partir de ahora háblame siempre de tú", True),
    ([], "nunca me pongas música antes de las nueve", True),
    ([], "soy alérgico a los frutos secos, tenlo en cuenta para las recetas", True),
    ([], "mi hermana Laura vive en Sevilla y viene en Navidad", True),
    ([], "I read light novels and I always buy physical copies in English when I can", True),
    ([], "la pasta la cuezo siempre nueve minutos", True),
    ([], "apaga la luz. por cierto, me he mudado a Valencia este mes", True),
    ([], "prefiero que las respuestas por voz sean cortas", True),
    ([("assistant", "¿A qué hora sueles levantarte entre semana?")], "a las siete siempre", True),
    ([("assistant", "¿Quieres que lo recuerde para otras veces?")], "sí, siempre que cocine arroz", True),
    ([], "my daughter Emma has type 1 diabetes, that's why I ask about glucose", True),
    ([], "estoy escribiendo una novela desde hace dos años, de ciencia ficción", True),
    ([], "no, me llamo Fran, no Francisco", True),
]

A_QS = {
    "fact": "Does `current` — read with `context` only to resolve what it refers to — state a lasting fact about the person who wrote it: their identity, work, home, health, family, relationships, possessions, skills or ongoing projects? A question, a command to a device, or something true only today is not a lasting fact.",
    "preference": "Does `current` — read with `context` only to resolve what it refers to — state a standing preference, habit or routine of the person who wrote it, something they say holds generally and not just for this request?",
    "instruction": "Does `current` — read with `context` only to resolve what it refers to — give the assistant a standing instruction for the future, such as 'always', 'never', 'from now on', or a correction of something the assistant has wrong about the person?",
}

# ---------- B: verification. (context, current, candidate, keep)
B_CASES = [
    ([], "is the complete X series purchasable in English on Amazon.es?", "User asks whether the complete X series is purchasable in English on Amazon.es", False),
    ([], "how do I switch the CoT role?", "User asks: 'how do I switch the CoT role'", False),
    ([], "my story feels incoherent, can subagents help with this setup?", "User feels their story lacks coherence and wants to use subagents to improve it", False),
    ([], "my story feels incoherent, can subagents help with this setup?", "Wants their creative story to feel more coherent while using this setup", False),
    ([], "¿qué tiempo hace el martes?", "Preguntó por el tiempo el martes", False),
    ([], "pídeme una pizza cuatro estaciones", "Pidió una pizza cuatro estaciones un viernes", False),
    ([], "ayer llegué tarde a casa, estoy reventado", "Ayer llegó tarde a casa", False),
    ([], "pon la serie esa en el salón", "Estaba viendo una serie el domingo por la tarde", False),
    ([], "enciende la luz del salón", "Le gusta tener la luz del salón encendida", False),
    ([], "trabajo en Globex como ingeniero de datos", "Trabaja en Initech como director de ventas", False),
    ([], "trabajo en Globex como ingeniero de datos", "Tiene dos hijos", False),
    ([("assistant", "Soy Nabu, tu asistente, y funciono con un modelo de lenguaje.")], "vale, gracias", "El asistente se llama Nabu y funciona con un modelo de lenguaje", False),
    ([("assistant", "A mucha gente le gusta el jazz para concentrarse.")], "pon algo para concentrarme", "Le gusta el jazz para concentrarse", False),
    ([], "trabajo en Globex como ingeniero de datos", "Trabaja en Globex como ingeniero de datos", True),
    ([], "a partir de ahora háblame siempre de tú", "Instrucción: tratarle siempre de tú", True),
    ([], "soy alérgico a los frutos secos, tenlo en cuenta para las recetas", "Es alérgico a los frutos secos", True),
    ([], "mi hermana Laura vive en Sevilla y viene en Navidad", "Su hermana Laura vive en Sevilla", True),
    ([], "I read light novels and I always buy physical copies in English when I can", "Reads light novels and prefers buying physical copies in English when available", True),
    ([], "la pasta la cuezo siempre nueve minutos", "Cuece la pasta siempre nueve minutos", True),
    ([], "apaga la luz. por cierto, me he mudado a Valencia este mes", "Se ha mudado a Valencia", True),
    ([("assistant", "¿A qué hora sueles levantarte entre semana?")], "a las siete siempre", "Se levanta a las siete entre semana", True),
    ([], "my daughter Emma has type 1 diabetes, that's why I ask about glucose", "Has a daughter, Emma, who has type 1 diabetes", True),
    ([], "estoy escribiendo una novela desde hace dos años, de ciencia ficción", "Está escribiendo una novela de ciencia ficción desde hace dos años", True),
    ([], "nunca me pongas música antes de las nueve", "Instrucción: no poner música antes de las nueve", True),
]

B_QS = {
    "supported": "`candidate` is a note an assistant wants to save about the person who wrote `current`. Did that person actually state what `candidate` says, in `current` (read with `context` only to resolve what it refers to)? Something only the assistant said, or something the note adds that the person never said, is not stated by them.",
    "about_user": "`candidate` is a note an assistant wants to save about the person who wrote `current`. Is `candidate` about that person — themselves, their life, their people, their preferences or their instructions — rather than about the assistant, the system, or the world in general?",
    "durable": "`candidate` is a note an assistant wants to save. Read six months from now, with no knowledge of this conversation, would `candidate` still tell something true and useful about the person? A one-off event, a mood, today's task or a single request would not.",
    "not_a_question": "`candidate` is a note an assistant wants to save about the person who wrote `current`. Is `candidate` something other than a restatement that the person asked, requested or ordered something? A note that only records that they asked or requested something is a restatement.",
}

# ---------- C: pairwise. (a, b, expected in {same, updates, distinct, unrelated}); link = same|updates
C_CASES = [
    ("Trabaja en Globex como ingeniero de datos", "Es ingeniero de datos en Globex", "same"),
    ("Viaja a Tokio", "Viaja a Tokio el 9 de abril", "same"),
    ("Es alérgico a los frutos secos", "No puede comer nueces ni almendras por alergia", "same"),
    ("Reads light novels", "Prefers buying light novels as physical copies in English", "same"),
    ("Su hermana Laura vive en Sevilla", "Tiene una hermana llamada Laura", "same"),
    ("Vive en Madrid", "Se ha mudado a Valencia", "updates"),
    ("Trabaja en Globex", "Dejó Globex y ahora trabaja en Initech", "updates"),
    ("Prefiere que le traten de usted", "Instrucción: tratarle siempre de tú", "updates"),
    ("Se levanta a las siete entre semana", "Ahora se levanta a las seis entre semana", "updates"),
    ("Su hermana Laura vive en Sevilla", "Su hermano Pablo vive en Bilbao", "distinct"),
    ("Es alérgico a los frutos secos", "Es intolerante a la lactosa", "distinct"),
    ("Cuece la pasta siempre nueve minutos", "Cuece el arroz dieciocho minutos", "distinct"),
    ("Trabaja en Globex como ingeniero de datos", "Está escribiendo una novela de ciencia ficción", "unrelated"),
    ("Prefiere respuestas por voz cortas", "Su hija Emma tiene diabetes tipo 1", "unrelated"),
    ("Likes jazz while working", "Vive en Valencia", "unrelated"),
    ("Le gusta el jazz para concentrarse", "Le gusta el jazz; escucha a Coltrane cuando trabaja", "same"),
]

C_Q = {"type": "choice",
       "instructions": "`a` and `b` are two notes saved about the same person. How are they related?",
       "criteria": {
           "same": "They state the same fact, or one is a more specific or less specific version of the other; keeping both would be redundant.",
           "updates": "They are about the same attribute of the person but disagree: one replaces, corrects or contradicts the other.",
           "distinct": "They are about a similar topic but state different facts that are both true at once; both should be kept.",
           "unrelated": "They are about different things.",
       }}


def state(ctx, cur, **extra):
    return {"context": [{"role": r, "text": t} for r, t in ctx], "current": cur, **extra}


def main():
    k = key()
    print(f"model={MODEL}\n\nA — GATE (skip only if every noul <= 0.1)")
    false_skips = skips = empties = 0
    for ctx, cur, durable in A_CASES:
        d = ask(state(ctx, cur), {n: {"type": "noul", "instructions": q} for n, q in A_QS.items()}, k)
        p = {n: d["answers"][n]["noul"] for n in A_QS}
        skip = max(p.values()) <= 0.1
        empties += not durable
        skips += skip and not durable
        false_skips += skip and durable
        flag = "FALSE-SKIP" if skip and durable else ("skip" if skip else "pass")
        print(f"{flag:10} durable={durable!s:5} max={max(p.values()):.2f} {p}  {cur!r}")
    print(f"\nA: false skips {false_skips}/{sum(1 for c in A_CASES if c[2])}   skipped {skips}/{empties} empty windows\n")

    print("B — VERIFY (keep iff every noul >= 0.5)")
    ok = 0
    for ctx, cur, cand, keep in B_CASES:
        d = ask(state(ctx, cur, candidate=cand), {n: {"type": "noul", "instructions": q} for n, q in B_QS.items()}, k)
        p = {n: round(d["answers"][n]["noul"], 2) for n in B_QS}
        got = min(p.values()) >= 0.5
        ok += got == keep
        print(f"{'ok ' if got == keep else 'BAD'} keep={keep!s:5} got={got!s:5} {p}  {cand!r}")
    print(f"\nB: {ok}/{len(B_CASES)}\n")

    print("C — PAIRS (link iff same|updates)")
    ok = link_ok = 0
    for a, b, want in C_CASES:
        d = ask({"a": a, "b": b}, {"rel": C_Q}, k)
        ans = d["answers"]["rel"]
        got = ans["choice"]
        ok += got == want
        l = (got in ("same", "updates")) == (want in ("same", "updates"))
        link_ok += l
        print(f"{'ok ' if got == want else ('~  ' if l else 'BAD')} want={want:9} got={got:9} ({ans['confidence']:.2f})  {a!r} | {b!r}")
    print(f"\nC: exact {ok}/{len(C_CASES)}   link decision {link_ok}/{len(C_CASES)}")
    print(f"\nlatency ms (warm): median {statistics.median(LAT):.0f}  max {max(LAT):.0f}")


if __name__ == "__main__":
    main()
