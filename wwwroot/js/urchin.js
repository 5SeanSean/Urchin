window.urchinHelpers = {

    initEyes: () => {
        if (window._urchinMouseHandler) {
            document.removeEventListener('mousemove', window._urchinMouseHandler);
            window._urchinMouseHandler = null;
        }

        const handleMouseMove = (e) => {
            const eyeWhites = document.querySelectorAll('.urchin-eye-white');
            if (eyeWhites.length === 0) return;

            eyeWhites.forEach(white => {
                const pupil = white.parentElement?.querySelector('.urchin-pupil');
                if (!pupil) return;

                const svg = white.ownerSVGElement;
                const whiteCx = parseFloat(white.getAttribute('cx')) || 0;
                const whiteCy = parseFloat(white.getAttribute('cy')) || 0;
                const maxOffset = 3.5;

                if (svg && svg.getScreenCTM) {
                    try {
                        const pt = svg.createSVGPoint();
                        pt.x = e.clientX;
                        pt.y = e.clientY;
                        const svgPt = pt.matrixTransform(svg.getScreenCTM().inverse());

                        const dx = svgPt.x - whiteCx;
                        const dy = svgPt.y - whiteCy;
                        const dist = Math.hypot(dx, dy);
                        const offset = Math.min(maxOffset, dist * 0.12);

                        const angle = Math.atan2(dy, dx);
                        const newCx = whiteCx + Math.cos(angle) * offset;
                        const newCy = whiteCy + Math.sin(angle) * offset;

                        pupil.setAttribute('cx', newCx);
                        pupil.setAttribute('cy', newCy);
                        return;
                    } catch (_) {}
                }

                // Fallback
                const rect = white.getBoundingClientRect();
                if (rect.width === 0) return;

                const cx = rect.left + rect.width / 2;
                const cy = rect.top + rect.height / 2;

                const angle = Math.atan2(e.clientY - cy, e.clientX - cx);
                const rawDist = Math.hypot(e.clientX - cx, e.clientY - cy);
                const maxPixelDist = rect.width * 0.12;
                const dist = Math.min(maxPixelDist, rawDist * 0.08);

                const dx = Math.cos(angle) * dist;
                const dy = Math.sin(angle) * dist;

                const scale = 20 / rect.width;
                const svgDx = dx * scale;
                const svgDy = dy * scale;

                pupil.setAttribute('cx', whiteCx + svgDx);
                pupil.setAttribute('cy', whiteCy + svgDy);
            });
        };

        window._urchinMouseHandler = handleMouseMove;
        document.addEventListener('mousemove', handleMouseMove);
        handleMouseMove({ clientX: window.innerWidth/2, clientY: window.innerHeight/2 });
    },

    wiggleOnce: (wrapperId) => {
        const el = document.getElementById(wrapperId);
        if (!el) return;
        el.classList.remove('urchin-wiggle-once');
        void el.offsetWidth;
        el.classList.add('urchin-wiggle-once');
        el.addEventListener('animationend', () => {
            el.classList.remove('urchin-wiggle-once');
        }, { once: true });
    },

    startWiggle: (wrapperId) => {
        const el = document.getElementById(wrapperId);
        if (el) el.classList.add('urchin-wiggle-loop');
    },

    stopWiggle: (wrapperId) => {
        const el = document.getElementById(wrapperId);
        if (el) el.classList.remove('urchin-wiggle-loop');
    },

    scrollToBottom: (elementId) => {
        const el = document.getElementById(elementId);
        if (el) el.scrollTop = el.scrollHeight;
    }
};