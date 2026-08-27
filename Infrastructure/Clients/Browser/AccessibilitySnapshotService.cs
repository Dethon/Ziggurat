using System.Text.Json;
using Microsoft.Playwright;

namespace Infrastructure.Clients.Browser;

public class AccessibilitySnapshotService
{
    private const string SnapshotScript = """
        (args) => {
            const selectorScope = typeof args === 'string' ? args : args?.scope;
            const preserveRefs = args?.preserveRefs === true;
            // Augment keeps every ref already on the document and stamps fresh numbers only onto
            // elements that have none — a non-navigating action must not invalidate the refs the
            // model is mid-flow with.
            const augmentRefs = args?.augmentRefs === true;
            // Numbers continue from where the session last stopped, so a ref stamped here is a
            // number no earlier page or snapshot in the session ever answered to.
            const startNumber = args?.startNumber ?? 1;
            if (!preserveRefs && !augmentRefs) {
                document.querySelectorAll('[data-ref]').forEach(el => el.removeAttribute('data-ref'));
            }
            let refCounter = 0;

            const implicitRoles = {
                'A': (el) => el.hasAttribute('href') ? 'link' : null,
                'BUTTON': () => 'button',
                'INPUT': (el) => {
                    const type = (el.getAttribute('type') || 'text').toLowerCase();
                    return ({ text:'textbox', search:'searchbox', email:'textbox', password:'textbox',
                        tel:'textbox', url:'textbox', number:'spinbutton', checkbox:'checkbox',
                        radio:'radio', range:'slider', submit:'button', reset:'button',
                        button:'button', image:'button' })[type] || 'textbox';
                },
                'SELECT': () => 'combobox',
                'TEXTAREA': () => 'textbox',
                'H1': () => 'heading', 'H2': () => 'heading', 'H3': () => 'heading',
                'H4': () => 'heading', 'H5': () => 'heading', 'H6': () => 'heading',
                'IMG': () => 'img',
                'NAV': () => 'navigation', 'MAIN': () => 'main',
                'HEADER': () => 'banner', 'FOOTER': () => 'contentinfo',
                'ASIDE': () => 'complementary', 'FORM': () => 'form',
                'TABLE': () => 'table', 'THEAD': () => 'rowgroup', 'TBODY': () => 'rowgroup',
                'TH': () => 'columnheader', 'TD': () => 'cell', 'TR': () => 'row',
                'UL': () => 'list', 'OL': () => 'list', 'LI': () => 'listitem',
                'OPTION': () => 'option', 'DIALOG': () => 'dialog',
                'DETAILS': () => 'group', 'SUMMARY': () => 'button',
                'PROGRESS': () => 'progressbar', 'METER': () => 'meter'
            };

            const interactiveRoles = new Set([
                'button','link','textbox','searchbox','combobox','checkbox','radio',
                'slider','spinbutton','switch','tab','menuitem','menuitemcheckbox',
                'menuitemradio','option','treeitem'
            ]);

            const structuralRoles = new Set([
                'heading','navigation','main','banner','contentinfo','complementary',
                'form','dialog','list','listitem','table','row','cell','columnheader',
                'img','group','tablist','tabpanel','menu','menubar','toolbar','tree',
                'grid','listbox','progressbar','meter','alert','status','region','rowgroup'
            ]);

            function getRole(el) {
                const explicit = el.getAttribute('role');
                if (explicit) return explicit;
                const fn = implicitRoles[el.tagName];
                return fn ? fn(el) : null;
            }

            function getName(el) {
                const ariaLabel = el.getAttribute('aria-label');
                if (ariaLabel) return ariaLabel.trim();

                const labelledBy = el.getAttribute('aria-labelledby');
                if (labelledBy) {
                    const text = labelledBy.split(/\s+/)
                        .map(id => document.getElementById(id)?.textContent?.trim())
                        .filter(Boolean).join(' ');
                    if (text) return text;
                }

                if (el.id) {
                    const label = document.querySelector(`label[for="${CSS.escape(el.id)}"]`);
                    if (label) return label.textContent.trim();
                }

                const parentLabel = el.closest('label');
                if (parentLabel && parentLabel !== el) {
                    const clone = parentLabel.cloneNode(true);
                    clone.querySelectorAll('input,select,textarea').forEach(c => c.remove());
                    const text = clone.textContent.trim();
                    if (text) return text;
                }

                const placeholder = el.getAttribute('placeholder');
                if (placeholder) return placeholder.trim();

                const title = el.getAttribute('title');
                if (title) return title.trim();

                let role = getRole(el);
                if (!role || (!interactiveRoles.has(role) && !structuralRoles.has(role))) {
                    role = inferClickable(el) || role;
                }
                if (['button','link','heading','tab','menuitem','option','cell','columnheader','listitem'].includes(role)) {
                    const text = el.textContent?.trim();
                    if (text && text.length <= 80) return text;
                    if (text) return text.substring(0, 77) + '...';
                }

                if (el.tagName === 'IMG') return el.getAttribute('alt')?.trim() || null;
                return null;
            }

            function getState(el) {
                const s = [];
                if (el.getAttribute('aria-expanded') === 'true') s.push('expanded');
                if (el.getAttribute('aria-expanded') === 'false') s.push('collapsed');
                if (el.hasAttribute('disabled') || el.getAttribute('aria-disabled') === 'true') s.push('disabled');
                if (el.hasAttribute('required') || el.getAttribute('aria-required') === 'true') s.push('required');
                if (el.getAttribute('aria-checked') === 'true' || el.checked) s.push('checked');
                if (el.getAttribute('aria-selected') === 'true' || el.selected) s.push('selected');
                if (el.getAttribute('aria-pressed') === 'true') s.push('pressed');
                if (el.hasAttribute('readonly') || el.getAttribute('aria-readonly') === 'true') s.push('readonly');
                const hm = el.tagName.match(/^H(\d)$/);
                if (hm) s.push('level=' + hm[1]);
                return s;
            }

            function getValue(el) {
                if (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA') return el.value || null;
                if (el.tagName === 'SELECT') {
                    const opt = el.options[el.selectedIndex];
                    return opt ? opt.text : null;
                }
                return null;
            }

            function isHidden(el) {
                if (el.tagName === 'SCRIPT' || el.tagName === 'STYLE' || el.tagName === 'NOSCRIPT') return true;
                if (el.hidden || el.getAttribute('aria-hidden') === 'true') return true;
                const cs = getComputedStyle(el);
                return cs.display === 'none' || cs.visibility === 'hidden';
            }

            // Must not be looser than Playwright's own definition of visible — a NON-EMPTY bounding
            // box, i.e. both dimensions positive. A ref is a promise that web_action can act on the
            // element, and it resolves one with WaitForAsync(Visible, 15s); advertising a 120x0
            // wrapper or a zero-size fixed element therefore buys a guaranteed 15-second dead wait
            // ending in "Operation timed out." rather than a usable action.
            function isVisible(el) {
                const rect = el.getBoundingClientRect();
                return rect.width > 0 && rect.height > 0;
            }

            function inferClickable(el) {
                if (getComputedStyle(el).cursor !== 'pointer') return null;
                const rect = el.getBoundingClientRect();
                if (rect.width === 0 || rect.height === 0) return null;
                // Skip large containers — real buttons are compact
                if (rect.width > 500 && rect.height > 100) return null;
                const text = el.textContent?.trim();
                if (!text || text.length > 40) return null;
                // Skip if it contains interactive children (they'll get their own refs)
                if (el.querySelector('a,button,input,select,textarea,[role="button"],[role="link"]')) return null;
                return 'button';
            }

            function buildTree(el) {
                if (isHidden(el)) return null;
                let role = getRole(el);
                // If the implicit role is not in our known sets, try inferring clickability
                if (!role || (!interactiveRoles.has(role) && !structuralRoles.has(role))) {
                    role = inferClickable(el) || role;
                }
                const children = Array.from(el.children)
                    .map(c => buildTree(c)).filter(Boolean);

                if (!role && children.length === 1) return children[0];
                if (!role && children.length === 0) return null;

                const isKnown = role && (interactiveRoles.has(role) || structuralRoles.has(role));
                const clickableStructural = new Set(['cell','listitem','columnheader']);

                if (isKnown) {
                    let ref = null;
                    if ((interactiveRoles.has(role) || clickableStructural.has(role)) && isVisible(el)) {
                        const existing = el.getAttribute('data-ref');
                        if ((preserveRefs || augmentRefs) && existing) {
                            ref = existing;
                        } else {
                            refCounter++;
                            ref = 'e-' + (startNumber + refCounter - 1);
                            if (!preserveRefs) el.setAttribute('data-ref', ref);
                        }
                    }

                    return { role, name: getName(el), state: getState(el),
                             value: getValue(el), ref, children };
                }

                return children.length > 0 ? { role: null, children } : null;
            }

            const collapseRoles = new Set(['list','listitem']);

            function format(node, indent) {
                if (!node) return '';
                if (!node.role) {
                    return node.children.map(c => format(c, indent)).filter(Boolean).join('\n');
                }
                // Collapse unnamed, refless list/listitem wrappers to reduce nesting noise
                if (collapseRoles.has(node.role) && !node.ref && !node.name
                    && node.state.length === 0 && !node.value) {
                    return node.children.map(c => format(c, indent)).filter(Boolean).join('\n');
                }
                let line = '  '.repeat(indent) + '- ' + node.role;
                if (node.name) line += ' "' + node.name.replace(/"/g, '\\"') + '"';
                if (node.ref) line += ' [ref=' + node.ref + ']';
                if (node.value) line += ': ' + node.value;
                for (const s of node.state) line += ' [' + s + ']';
                const cl = node.children.map(c => format(c, indent + 1)).filter(Boolean);
                return cl.length > 0 ? line + '\n' + cl.join('\n') : line;
            }

            if (!selectorScope) {
                const tree = buildTree(document.body);
                return { snapshot: tree ? format(tree, 0) : '', refCount: refCounter };
            }
            const roots = [...document.querySelectorAll(selectorScope)];
            if (roots.length === 0) return { snapshot: '', refCount: 0 };
            const parts = roots.map(r => buildTree(r)).filter(Boolean).map(t => format(t, 0)).filter(Boolean);
            return { snapshot: parts.join('\n'), refCount: refCounter };
        }
        """;

    private const string FindContainerScript = """
        (selector) => {
            const el = document.querySelector(selector);
            if (!el) return null;
            const tags = new Set(['FORM','SECTION','ARTICLE','MAIN','DIALOG','FIELDSET','NAV','ASIDE','HEADER','FOOTER']);
            const roles = new Set(['dialog','form','region','listbox','menu','navigation','complementary','banner','contentinfo']);

            function toSelector(node) {
                if (node.id) return '#' + CSS.escape(node.id);
                const idx = Array.from(node.parentElement?.children || [])
                    .filter(c => c.tagName === node.tagName).indexOf(node);
                return node.tagName.toLowerCase() + ':nth-of-type(' + (idx + 1) + ')';
            }

            let container = el.parentElement;
            for (let i = 0; i < 8 && container && container !== document.body; i++) {
                if (tags.has(container.tagName) ||
                    roles.has(container.getAttribute('role'))) {
                    return toSelector(container);
                }
                container = container.parentElement;
            }
            return null;
        }
        """;

    public async Task<SnapshotCaptureResult> CaptureAsync(
        IPage page, string? selectorScope, string sessionId, bool preserveRefs = false,
        int startNumber = 1, bool augmentRefs = false)
    {
        var args = selectorScope is not null || preserveRefs || augmentRefs || startNumber != 1
            ? new { scope = selectorScope, preserveRefs, augmentRefs, startNumber }
            : null;

        var result = await page.EvaluateAsync<JsonElement>(SnapshotScript, args);
        var snapshot = result.GetProperty("snapshot").GetString() ?? "";
        var refCount = result.GetProperty("refCount").GetInt32();
        return new SnapshotCaptureResult(snapshot, refCount);
    }

    public async Task<SnapshotCaptureResult> CaptureScopedAsync(
        IPage page, string targetSelector, string sessionId)
    {
        var containerSelector = await page.EvaluateAsync<string?>(
            FindContainerScript, targetSelector);

        return await CaptureAsync(page, containerSelector, sessionId);
    }

    public static ILocator ResolveRef(IPage page, string @ref)
        => page.Locator($"[data-ref='{@ref}']");

    public record SnapshotCaptureResult(string Snapshot, int RefCount);
}