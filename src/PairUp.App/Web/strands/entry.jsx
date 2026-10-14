import React from 'react';
import { createRoot } from 'react-dom/client';
import { flushSync } from 'react-dom';
import Strands from '@shengyu/strands-adapted';

class StrandsBoundary extends React.Component {
  state = { failed: false };
  static getDerivedStateFromError() { return { failed: true }; }
  componentDidCatch(error) { this.props.onError?.(String(error)); }
  render() { return this.state.failed ? null : this.props.children; }
}

// This isolated React root never shares state or packages with the original
// liquid-glass runtime. Its only responsibility is the official Strands effect.
window.ShengyuStrandsRuntime = Object.freeze({
  mount(container, props, onError) {
    let disposed = false, failure = null;
    const root = createRoot(container);
    const failed = error => { failure = error; onError?.(error); };
    const destroy = () => { if (disposed) return; disposed = true; flushSync(() => root.unmount()); };
    const render = next => {
      if (!disposed) flushSync(() => root.render(<StrandsBoundary onError={failed}><Strands {...next} /></StrandsBoundary>));
    };
    try {
      render(props);
      // An effect may fail inside flushSync without throwing through the caller.
      // Do not return an apparently mounted instance after Boundary caught it.
      if (failure) throw new Error(failure);
    } catch (error) { destroy(); throw error; }
    return Object.freeze({
      update: render,
      destroy
    });
  }
});
