import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach, vi } from 'vitest'

afterEach(() => cleanup())

// jsdom has no matchMedia; the theme hook needs it. Default: system prefers light.
if (!window.matchMedia) {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addEventListener: () => {},
    removeEventListener: () => {},
    addListener: () => {},
    removeListener: () => {},
    dispatchEvent: () => false,
  }))
}

// React Flow (workflow designer) needs these browser APIs, which jsdom lacks. Minimal no-op stand-ins:
// nothing is measured or laid out in jsdom, so tests check behaviour, not geometry.
if (!('ResizeObserver' in window)) {
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  )
}
if (!('DOMMatrixReadOnly' in window)) {
  vi.stubGlobal(
    'DOMMatrixReadOnly',
    class {
      m22 = 1
    },
  )
}
