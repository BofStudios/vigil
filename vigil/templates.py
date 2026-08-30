"""Code templates (plugin scaffold, etc.)."""

PLUGIN_TEMPLATE = '''"""Vigil plugin: {name}

This file lives in ~/.vigil/plugins/ so Vigil loads it automatically at startup.
Tools still pass through the security layer: call ctx.guard for destructive work.

Verify with:  vigil tools
"""

from vigil.security import Risk
from vigil.tools import ToolContext, ToolSpec


def {name}(ctx: ToolContext, text: str = "") -> str:
    """What the tool does. The returned string is fed back to the model.

    What you can reach through ctx:
      ctx.cwd      -> current working directory (Path)
      ctx.config   -> user settings
      ctx.guard    -> approval engine (use it for risky actions)
      ctx.provider -> AI provider (ctx.provider.vision(...) for image analysis)
    """
    return "hello " + (text or "world")


TOOLS = [
    ToolSpec(
        name="{name}",
        description="Example plugin tool. Write the description so the model knows when to use it.",
        parameters={{
            "type": "object",
            "properties": {{
                "text": {{"type": "string", "description": "Text to greet"}},
            }},
        }},
        handler={name},
        group="plugin",
        risk=Risk.SAFE,
    ),
]
'''

RISKY_PLUGIN_EXAMPLE = '''# Example of a tool that does something destructive - always ask first:
#
#     from vigil.tools import PermissionDenied
#
#     allowed, reason = ctx.guard.check_action(
#         "tool_name", "what will happen", Risk.HIGH, "why it is risky"
#     )
#     if not allowed:
#         raise PermissionDenied(reason)
'''
