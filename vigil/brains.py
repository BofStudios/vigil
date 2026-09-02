"""The two ways Vigil can think.

One follows instructions; the other is handed a problem and works out the route
itself. This is deliberately a small, declarative module: the words shown in the
picker and the behaviour they promise live in the same place, so the description
cannot drift away from what actually happens.

What a brain changes: the approach section of the system prompt, how many steps
a run may take, whether the planning tools are offered at all, and which model
does the thinking.

What a brain does NOT change: anything about security. Blocked actions stay
blocked, and mouse, keyboard and screen are confirmed every time in both. The
autonomous one is riskier because it acts more, not because it asks less.
"""

from __future__ import annotations

from dataclasses import dataclass

DIRECT = "direct"
AUTONOMOUS = "autonomous"


@dataclass(frozen=True)
class Brain:
    """One way of working, and the words that describe it."""

    key: str
    name: str
    tagline: str
    summary: str
    model: str          # preferred Groq model; other providers keep their own
    max_steps: int
    plans: bool
    approach: str
    warning: str = ""

    def describe(self) -> dict:
        """What the picker needs to draw this option."""
        return {
            "key": self.key,
            "name": self.name,
            "tagline": self.tagline,
            "summary": self.summary,
            "model": self.model,
            "warning": self.warning,
        }


_DIRECT_APPROACH = """APPROACH - DIRECT
- Do the thing that was asked, and only that. Do not widen the job.
- Take the shortest correct route: fewest tools, fewest steps.
- Ask when a request is ambiguous rather than guessing which reading was meant.
- Do not write a plan. At this size it is ceremony, and the user wants the result.
- Stop when the thing asked for is done. Do not go looking for more to do."""


_AUTONOMOUS_APPROACH = """APPROACH - AUTONOMOUS
- You are handed problems, not instructions. Work out the route yourself.
- Call create_plan first for anything needing three or more steps, and mark each
  step with update_plan as it finishes - the user reads the plan to see where you are.
- Find out before you decide. Check what is actually there instead of assuming.
- When something fails, work out why and try a different approach. Never repeat
  the call that just failed.
- Verify your own work at the end, and say plainly what you could not confirm.
- You may take several steps without checking in, but report what you did."""


BRAINS = {
    DIRECT: Brain(
        key=DIRECT,
        name="Direct",
        tagline="Does what you ask",
        summary=(
            "Follows your instruction step by step and stops when it is finished. "
            "Best for everyday jobs - open this, rename that, find that file."
        ),
        model="openai/gpt-oss-20b",
        max_steps=14,
        plans=False,
        approach=_DIRECT_APPROACH,
    ),
    AUTONOMOUS: Brain(
        key=AUTONOMOUS,
        name="Autonomous",
        tagline="Works out how",
        summary=(
            "Give it a problem rather than an instruction and it plans a route, "
            "tries things, and changes course when they do not work. Best for "
            "when you do not know the steps yourself."
        ),
        model="openai/gpt-oss-120b",
        max_steps=40,
        plans=True,
        warning=(
            "Takes many more actions on its own before it comes back to you. It "
            "still asks before touching your mouse, keyboard or screen - that never "
            "changes - but it will travel further on a wrong idea before you see it."
        ),
        approach=_AUTONOMOUS_APPROACH,
    ),
}

DEFAULT = DIRECT


def get(key: str) -> Brain:
    """The named brain, or the default when the name means nothing."""
    return BRAINS.get((key or "").strip().lower(), BRAINS[DEFAULT])


def names() -> list:
    """The keys, in the order they should be offered."""
    return [DIRECT, AUTONOMOUS]


def describe_all() -> list:
    """Everything the picker needs, in order."""
    return [BRAINS[key].describe() for key in names()]
