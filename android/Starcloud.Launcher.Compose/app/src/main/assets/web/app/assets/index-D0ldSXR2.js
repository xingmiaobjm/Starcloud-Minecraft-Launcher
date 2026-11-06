function getDefaultExportFromCjs(x2) {
  return x2 && x2.__esModule && Object.prototype.hasOwnProperty.call(x2, "default") ? x2["default"] : x2;
}
var jsxRuntime = { exports: {} };
var reactJsxRuntime_production_min = {};
var react = { exports: {} };
var react_production_min = {};
/**
 * @license React
 * react.production.min.js
 *
 * Copyright (c) Facebook, Inc. and its affiliates.
 *
 * This source code is licensed under the MIT license found in the
 * LICENSE file in the root directory of this source tree.
 */
var l$1 = Symbol.for("react.element"), n$1 = Symbol.for("react.portal"), p$2 = Symbol.for("react.fragment"), q$1 = Symbol.for("react.strict_mode"), r = Symbol.for("react.profiler"), t = Symbol.for("react.provider"), u = Symbol.for("react.context"), v$1 = Symbol.for("react.forward_ref"), w = Symbol.for("react.suspense"), x = Symbol.for("react.memo"), y = Symbol.for("react.lazy"), z$1 = Symbol.iterator;
function A$1(a) {
  if (null === a || "object" !== typeof a) return null;
  a = z$1 && a[z$1] || a["@@iterator"];
  return "function" === typeof a ? a : null;
}
var B$1 = { isMounted: function() {
  return false;
}, enqueueForceUpdate: function() {
}, enqueueReplaceState: function() {
}, enqueueSetState: function() {
} }, C$1 = Object.assign, D$1 = {};
function E$1(a, b, e) {
  this.props = a;
  this.context = b;
  this.refs = D$1;
  this.updater = e || B$1;
}
E$1.prototype.isReactComponent = {};
E$1.prototype.setState = function(a, b) {
  if ("object" !== typeof a && "function" !== typeof a && null != a) throw Error("setState(...): takes an object of state variables to update or a function which returns an object of state variables.");
  this.updater.enqueueSetState(this, a, b, "setState");
};
E$1.prototype.forceUpdate = function(a) {
  this.updater.enqueueForceUpdate(this, a, "forceUpdate");
};
function F() {
}
F.prototype = E$1.prototype;
function G$1(a, b, e) {
  this.props = a;
  this.context = b;
  this.refs = D$1;
  this.updater = e || B$1;
}
var H$1 = G$1.prototype = new F();
H$1.constructor = G$1;
C$1(H$1, E$1.prototype);
H$1.isPureReactComponent = true;
var I$1 = Array.isArray, J = Object.prototype.hasOwnProperty, K$1 = { current: null }, L$1 = { key: true, ref: true, __self: true, __source: true };
function M$1(a, b, e) {
  var d, c = {}, k2 = null, h = null;
  if (null != b) for (d in void 0 !== b.ref && (h = b.ref), void 0 !== b.key && (k2 = "" + b.key), b) J.call(b, d) && !L$1.hasOwnProperty(d) && (c[d] = b[d]);
  var g = arguments.length - 2;
  if (1 === g) c.children = e;
  else if (1 < g) {
    for (var f2 = Array(g), m2 = 0; m2 < g; m2++) f2[m2] = arguments[m2 + 2];
    c.children = f2;
  }
  if (a && a.defaultProps) for (d in g = a.defaultProps, g) void 0 === c[d] && (c[d] = g[d]);
  return { $$typeof: l$1, type: a, key: k2, ref: h, props: c, _owner: K$1.current };
}
function N$1(a, b) {
  return { $$typeof: l$1, type: a.type, key: b, ref: a.ref, props: a.props, _owner: a._owner };
}
function O$1(a) {
  return "object" === typeof a && null !== a && a.$$typeof === l$1;
}
function escape(a) {
  var b = { "=": "=0", ":": "=2" };
  return "$" + a.replace(/[=:]/g, function(a2) {
    return b[a2];
  });
}
var P$1 = /\/+/g;
function Q$1(a, b) {
  return "object" === typeof a && null !== a && null != a.key ? escape("" + a.key) : b.toString(36);
}
function R$1(a, b, e, d, c) {
  var k2 = typeof a;
  if ("undefined" === k2 || "boolean" === k2) a = null;
  var h = false;
  if (null === a) h = true;
  else switch (k2) {
    case "string":
    case "number":
      h = true;
      break;
    case "object":
      switch (a.$$typeof) {
        case l$1:
        case n$1:
          h = true;
      }
  }
  if (h) return h = a, c = c(h), a = "" === d ? "." + Q$1(h, 0) : d, I$1(c) ? (e = "", null != a && (e = a.replace(P$1, "$&/") + "/"), R$1(c, b, e, "", function(a2) {
    return a2;
  })) : null != c && (O$1(c) && (c = N$1(c, e + (!c.key || h && h.key === c.key ? "" : ("" + c.key).replace(P$1, "$&/") + "/") + a)), b.push(c)), 1;
  h = 0;
  d = "" === d ? "." : d + ":";
  if (I$1(a)) for (var g = 0; g < a.length; g++) {
    k2 = a[g];
    var f2 = d + Q$1(k2, g);
    h += R$1(k2, b, e, f2, c);
  }
  else if (f2 = A$1(a), "function" === typeof f2) for (a = f2.call(a), g = 0; !(k2 = a.next()).done; ) k2 = k2.value, f2 = d + Q$1(k2, g++), h += R$1(k2, b, e, f2, c);
  else if ("object" === k2) throw b = String(a), Error("Objects are not valid as a React child (found: " + ("[object Object]" === b ? "object with keys {" + Object.keys(a).join(", ") + "}" : b) + "). If you meant to render a collection of children, use an array instead.");
  return h;
}
function S$1(a, b, e) {
  if (null == a) return a;
  var d = [], c = 0;
  R$1(a, d, "", "", function(a2) {
    return b.call(e, a2, c++);
  });
  return d;
}
function T$1(a) {
  if (-1 === a._status) {
    var b = a._result;
    b = b();
    b.then(function(b2) {
      if (0 === a._status || -1 === a._status) a._status = 1, a._result = b2;
    }, function(b2) {
      if (0 === a._status || -1 === a._status) a._status = 2, a._result = b2;
    });
    -1 === a._status && (a._status = 0, a._result = b);
  }
  if (1 === a._status) return a._result.default;
  throw a._result;
}
var U$1 = { current: null }, V$1 = { transition: null }, W$1 = { ReactCurrentDispatcher: U$1, ReactCurrentBatchConfig: V$1, ReactCurrentOwner: K$1 };
function X$1() {
  throw Error("act(...) is not supported in production builds of React.");
}
react_production_min.Children = { map: S$1, forEach: function(a, b, e) {
  S$1(a, function() {
    b.apply(this, arguments);
  }, e);
}, count: function(a) {
  var b = 0;
  S$1(a, function() {
    b++;
  });
  return b;
}, toArray: function(a) {
  return S$1(a, function(a2) {
    return a2;
  }) || [];
}, only: function(a) {
  if (!O$1(a)) throw Error("React.Children.only expected to receive a single React element child.");
  return a;
} };
react_production_min.Component = E$1;
react_production_min.Fragment = p$2;
react_production_min.Profiler = r;
react_production_min.PureComponent = G$1;
react_production_min.StrictMode = q$1;
react_production_min.Suspense = w;
react_production_min.__SECRET_INTERNALS_DO_NOT_USE_OR_YOU_WILL_BE_FIRED = W$1;
react_production_min.act = X$1;
react_production_min.cloneElement = function(a, b, e) {
  if (null === a || void 0 === a) throw Error("React.cloneElement(...): The argument must be a React element, but you passed " + a + ".");
  var d = C$1({}, a.props), c = a.key, k2 = a.ref, h = a._owner;
  if (null != b) {
    void 0 !== b.ref && (k2 = b.ref, h = K$1.current);
    void 0 !== b.key && (c = "" + b.key);
    if (a.type && a.type.defaultProps) var g = a.type.defaultProps;
    for (f2 in b) J.call(b, f2) && !L$1.hasOwnProperty(f2) && (d[f2] = void 0 === b[f2] && void 0 !== g ? g[f2] : b[f2]);
  }
  var f2 = arguments.length - 2;
  if (1 === f2) d.children = e;
  else if (1 < f2) {
    g = Array(f2);
    for (var m2 = 0; m2 < f2; m2++) g[m2] = arguments[m2 + 2];
    d.children = g;
  }
  return { $$typeof: l$1, type: a.type, key: c, ref: k2, props: d, _owner: h };
};
react_production_min.createContext = function(a) {
  a = { $$typeof: u, _currentValue: a, _currentValue2: a, _threadCount: 0, Provider: null, Consumer: null, _defaultValue: null, _globalName: null };
  a.Provider = { $$typeof: t, _context: a };
  return a.Consumer = a;
};
react_production_min.createElement = M$1;
react_production_min.createFactory = function(a) {
  var b = M$1.bind(null, a);
  b.type = a;
  return b;
};
react_production_min.createRef = function() {
  return { current: null };
};
react_production_min.forwardRef = function(a) {
  return { $$typeof: v$1, render: a };
};
react_production_min.isValidElement = O$1;
react_production_min.lazy = function(a) {
  return { $$typeof: y, _payload: { _status: -1, _result: a }, _init: T$1 };
};
react_production_min.memo = function(a, b) {
  return { $$typeof: x, type: a, compare: void 0 === b ? null : b };
};
react_production_min.startTransition = function(a) {
  var b = V$1.transition;
  V$1.transition = {};
  try {
    a();
  } finally {
    V$1.transition = b;
  }
};
react_production_min.unstable_act = X$1;
react_production_min.useCallback = function(a, b) {
  return U$1.current.useCallback(a, b);
};
react_production_min.useContext = function(a) {
  return U$1.current.useContext(a);
};
react_production_min.useDebugValue = function() {
};
react_production_min.useDeferredValue = function(a) {
  return U$1.current.useDeferredValue(a);
};
react_production_min.useEffect = function(a, b) {
  return U$1.current.useEffect(a, b);
};
react_production_min.useId = function() {
  return U$1.current.useId();
};
react_production_min.useImperativeHandle = function(a, b, e) {
  return U$1.current.useImperativeHandle(a, b, e);
};
react_production_min.useInsertionEffect = function(a, b) {
  return U$1.current.useInsertionEffect(a, b);
};
react_production_min.useLayoutEffect = function(a, b) {
  return U$1.current.useLayoutEffect(a, b);
};
react_production_min.useMemo = function(a, b) {
  return U$1.current.useMemo(a, b);
};
react_production_min.useReducer = function(a, b, e) {
  return U$1.current.useReducer(a, b, e);
};
react_production_min.useRef = function(a) {
  return U$1.current.useRef(a);
};
react_production_min.useState = function(a) {
  return U$1.current.useState(a);
};
react_production_min.useSyncExternalStore = function(a, b, e) {
  return U$1.current.useSyncExternalStore(a, b, e);
};
react_production_min.useTransition = function() {
  return U$1.current.useTransition();
};
react_production_min.version = "18.3.1";
{
  react.exports = react_production_min;
}
var reactExports = react.exports;
const React = /* @__PURE__ */ getDefaultExportFromCjs(reactExports);
/**
 * @license React
 * react-jsx-runtime.production.min.js
 *
 * Copyright (c) Facebook, Inc. and its affiliates.
 *
 * This source code is licensed under the MIT license found in the
 * LICENSE file in the root directory of this source tree.
 */
var f = reactExports, k = Symbol.for("react.element"), l = Symbol.for("react.fragment"), m$1 = Object.prototype.hasOwnProperty, n = f.__SECRET_INTERNALS_DO_NOT_USE_OR_YOU_WILL_BE_FIRED.ReactCurrentOwner, p$1 = { key: true, ref: true, __self: true, __source: true };
function q(c, a, g) {
  var b, d = {}, e = null, h = null;
  void 0 !== g && (e = "" + g);
  void 0 !== a.key && (e = "" + a.key);
  void 0 !== a.ref && (h = a.ref);
  for (b in a) m$1.call(a, b) && !p$1.hasOwnProperty(b) && (d[b] = a[b]);
  if (c && c.defaultProps) for (b in a = c.defaultProps, a) void 0 === d[b] && (d[b] = a[b]);
  return { $$typeof: k, type: c, key: e, ref: h, props: d, _owner: n.current };
}
reactJsxRuntime_production_min.Fragment = l;
reactJsxRuntime_production_min.jsx = q;
reactJsxRuntime_production_min.jsxs = q;
{
  jsxRuntime.exports = reactJsxRuntime_production_min;
}
var jsxRuntimeExports = jsxRuntime.exports;
var client = {};
var reactDom = { exports: {} };
var reactDom_production_min = {};
var scheduler = { exports: {} };
var scheduler_production_min = {};
/**
 * @license React
 * scheduler.production.min.js
 *
 * Copyright (c) Facebook, Inc. and its affiliates.
 *
 * This source code is licensed under the MIT license found in the
 * LICENSE file in the root directory of this source tree.
 */
(function(exports) {
  function f2(a, b) {
    var c = a.length;
    a.push(b);
    a: for (; 0 < c; ) {
      var d = c - 1 >>> 1, e = a[d];
      if (0 < g(e, b)) a[d] = b, a[c] = e, c = d;
      else break a;
    }
  }
  function h(a) {
    return 0 === a.length ? null : a[0];
  }
  function k2(a) {
    if (0 === a.length) return null;
    var b = a[0], c = a.pop();
    if (c !== b) {
      a[0] = c;
      a: for (var d = 0, e = a.length, w2 = e >>> 1; d < w2; ) {
        var m2 = 2 * (d + 1) - 1, C2 = a[m2], n2 = m2 + 1, x2 = a[n2];
        if (0 > g(C2, c)) n2 < e && 0 > g(x2, C2) ? (a[d] = x2, a[n2] = c, d = n2) : (a[d] = C2, a[m2] = c, d = m2);
        else if (n2 < e && 0 > g(x2, c)) a[d] = x2, a[n2] = c, d = n2;
        else break a;
      }
    }
    return b;
  }
  function g(a, b) {
    var c = a.sortIndex - b.sortIndex;
    return 0 !== c ? c : a.id - b.id;
  }
  if ("object" === typeof performance && "function" === typeof performance.now) {
    var l2 = performance;
    exports.unstable_now = function() {
      return l2.now();
    };
  } else {
    var p2 = Date, q2 = p2.now();
    exports.unstable_now = function() {
      return p2.now() - q2;
    };
  }
  var r2 = [], t2 = [], u2 = 1, v2 = null, y2 = 3, z2 = false, A2 = false, B2 = false, D2 = "function" === typeof setTimeout ? setTimeout : null, E2 = "function" === typeof clearTimeout ? clearTimeout : null, F2 = "undefined" !== typeof setImmediate ? setImmediate : null;
  "undefined" !== typeof navigator && void 0 !== navigator.scheduling && void 0 !== navigator.scheduling.isInputPending && navigator.scheduling.isInputPending.bind(navigator.scheduling);
  function G2(a) {
    for (var b = h(t2); null !== b; ) {
      if (null === b.callback) k2(t2);
      else if (b.startTime <= a) k2(t2), b.sortIndex = b.expirationTime, f2(r2, b);
      else break;
      b = h(t2);
    }
  }
  function H2(a) {
    B2 = false;
    G2(a);
    if (!A2) if (null !== h(r2)) A2 = true, I2(J2);
    else {
      var b = h(t2);
      null !== b && K2(H2, b.startTime - a);
    }
  }
  function J2(a, b) {
    A2 = false;
    B2 && (B2 = false, E2(L2), L2 = -1);
    z2 = true;
    var c = y2;
    try {
      G2(b);
      for (v2 = h(r2); null !== v2 && (!(v2.expirationTime > b) || a && !M2()); ) {
        var d = v2.callback;
        if ("function" === typeof d) {
          v2.callback = null;
          y2 = v2.priorityLevel;
          var e = d(v2.expirationTime <= b);
          b = exports.unstable_now();
          "function" === typeof e ? v2.callback = e : v2 === h(r2) && k2(r2);
          G2(b);
        } else k2(r2);
        v2 = h(r2);
      }
      if (null !== v2) var w2 = true;
      else {
        var m2 = h(t2);
        null !== m2 && K2(H2, m2.startTime - b);
        w2 = false;
      }
      return w2;
    } finally {
      v2 = null, y2 = c, z2 = false;
    }
  }
  var N2 = false, O2 = null, L2 = -1, P2 = 5, Q2 = -1;
  function M2() {
    return exports.unstable_now() - Q2 < P2 ? false : true;
  }
  function R2() {
    if (null !== O2) {
      var a = exports.unstable_now();
      Q2 = a;
      var b = true;
      try {
        b = O2(true, a);
      } finally {
        b ? S2() : (N2 = false, O2 = null);
      }
    } else N2 = false;
  }
  var S2;
  if ("function" === typeof F2) S2 = function() {
    F2(R2);
  };
  else if ("undefined" !== typeof MessageChannel) {
    var T2 = new MessageChannel(), U2 = T2.port2;
    T2.port1.onmessage = R2;
    S2 = function() {
      U2.postMessage(null);
    };
  } else S2 = function() {
    D2(R2, 0);
  };
  function I2(a) {
    O2 = a;
    N2 || (N2 = true, S2());
  }
  function K2(a, b) {
    L2 = D2(function() {
      a(exports.unstable_now());
    }, b);
  }
  exports.unstable_IdlePriority = 5;
  exports.unstable_ImmediatePriority = 1;
  exports.unstable_LowPriority = 4;
  exports.unstable_NormalPriority = 3;
  exports.unstable_Profiling = null;
  exports.unstable_UserBlockingPriority = 2;
  exports.unstable_cancelCallback = function(a) {
    a.callback = null;
  };
  exports.unstable_continueExecution = function() {
    A2 || z2 || (A2 = true, I2(J2));
  };
  exports.unstable_forceFrameRate = function(a) {
    0 > a || 125 < a ? console.error("forceFrameRate takes a positive int between 0 and 125, forcing frame rates higher than 125 fps is not supported") : P2 = 0 < a ? Math.floor(1e3 / a) : 5;
  };
  exports.unstable_getCurrentPriorityLevel = function() {
    return y2;
  };
  exports.unstable_getFirstCallbackNode = function() {
    return h(r2);
  };
  exports.unstable_next = function(a) {
    switch (y2) {
      case 1:
      case 2:
      case 3:
        var b = 3;
        break;
      default:
        b = y2;
    }
    var c = y2;
    y2 = b;
    try {
      return a();
    } finally {
      y2 = c;
    }
  };
  exports.unstable_pauseExecution = function() {
  };
  exports.unstable_requestPaint = function() {
  };
  exports.unstable_runWithPriority = function(a, b) {
    switch (a) {
      case 1:
      case 2:
      case 3:
      case 4:
      case 5:
        break;
      default:
        a = 3;
    }
    var c = y2;
    y2 = a;
    try {
      return b();
    } finally {
      y2 = c;
    }
  };
  exports.unstable_scheduleCallback = function(a, b, c) {
    var d = exports.unstable_now();
    "object" === typeof c && null !== c ? (c = c.delay, c = "number" === typeof c && 0 < c ? d + c : d) : c = d;
    switch (a) {
      case 1:
        var e = -1;
        break;
      case 2:
        e = 250;
        break;
      case 5:
        e = 1073741823;
        break;
      case 4:
        e = 1e4;
        break;
      default:
        e = 5e3;
    }
    e = c + e;
    a = { id: u2++, callback: b, priorityLevel: a, startTime: c, expirationTime: e, sortIndex: -1 };
    c > d ? (a.sortIndex = c, f2(t2, a), null === h(r2) && a === h(t2) && (B2 ? (E2(L2), L2 = -1) : B2 = true, K2(H2, c - d))) : (a.sortIndex = e, f2(r2, a), A2 || z2 || (A2 = true, I2(J2)));
    return a;
  };
  exports.unstable_shouldYield = M2;
  exports.unstable_wrapCallback = function(a) {
    var b = y2;
    return function() {
      var c = y2;
      y2 = b;
      try {
        return a.apply(this, arguments);
      } finally {
        y2 = c;
      }
    };
  };
})(scheduler_production_min);
{
  scheduler.exports = scheduler_production_min;
}
var schedulerExports = scheduler.exports;
/**
 * @license React
 * react-dom.production.min.js
 *
 * Copyright (c) Facebook, Inc. and its affiliates.
 *
 * This source code is licensed under the MIT license found in the
 * LICENSE file in the root directory of this source tree.
 */
var aa = reactExports, ca = schedulerExports;
function p(a) {
  for (var b = "https://reactjs.org/docs/error-decoder.html?invariant=" + a, c = 1; c < arguments.length; c++) b += "&args[]=" + encodeURIComponent(arguments[c]);
  return "Minified React error #" + a + "; visit " + b + " for the full message or use the non-minified dev environment for full errors and additional helpful warnings.";
}
var da = /* @__PURE__ */ new Set(), ea = {};
function fa(a, b) {
  ha(a, b);
  ha(a + "Capture", b);
}
function ha(a, b) {
  ea[a] = b;
  for (a = 0; a < b.length; a++) da.add(b[a]);
}
var ia = !("undefined" === typeof window || "undefined" === typeof window.document || "undefined" === typeof window.document.createElement), ja = Object.prototype.hasOwnProperty, ka = /^[:A-Z_a-z\u00C0-\u00D6\u00D8-\u00F6\u00F8-\u02FF\u0370-\u037D\u037F-\u1FFF\u200C-\u200D\u2070-\u218F\u2C00-\u2FEF\u3001-\uD7FF\uF900-\uFDCF\uFDF0-\uFFFD][:A-Z_a-z\u00C0-\u00D6\u00D8-\u00F6\u00F8-\u02FF\u0370-\u037D\u037F-\u1FFF\u200C-\u200D\u2070-\u218F\u2C00-\u2FEF\u3001-\uD7FF\uF900-\uFDCF\uFDF0-\uFFFD\-.0-9\u00B7\u0300-\u036F\u203F-\u2040]*$/, la = {}, ma = {};
function oa(a) {
  if (ja.call(ma, a)) return true;
  if (ja.call(la, a)) return false;
  if (ka.test(a)) return ma[a] = true;
  la[a] = true;
  return false;
}
function pa(a, b, c, d) {
  if (null !== c && 0 === c.type) return false;
  switch (typeof b) {
    case "function":
    case "symbol":
      return true;
    case "boolean":
      if (d) return false;
      if (null !== c) return !c.acceptsBooleans;
      a = a.toLowerCase().slice(0, 5);
      return "data-" !== a && "aria-" !== a;
    default:
      return false;
  }
}
function qa(a, b, c, d) {
  if (null === b || "undefined" === typeof b || pa(a, b, c, d)) return true;
  if (d) return false;
  if (null !== c) switch (c.type) {
    case 3:
      return !b;
    case 4:
      return false === b;
    case 5:
      return isNaN(b);
    case 6:
      return isNaN(b) || 1 > b;
  }
  return false;
}
function v(a, b, c, d, e, f2, g) {
  this.acceptsBooleans = 2 === b || 3 === b || 4 === b;
  this.attributeName = d;
  this.attributeNamespace = e;
  this.mustUseProperty = c;
  this.propertyName = a;
  this.type = b;
  this.sanitizeURL = f2;
  this.removeEmptyString = g;
}
var z = {};
"children dangerouslySetInnerHTML defaultValue defaultChecked innerHTML suppressContentEditableWarning suppressHydrationWarning style".split(" ").forEach(function(a) {
  z[a] = new v(a, 0, false, a, null, false, false);
});
[["acceptCharset", "accept-charset"], ["className", "class"], ["htmlFor", "for"], ["httpEquiv", "http-equiv"]].forEach(function(a) {
  var b = a[0];
  z[b] = new v(b, 1, false, a[1], null, false, false);
});
["contentEditable", "draggable", "spellCheck", "value"].forEach(function(a) {
  z[a] = new v(a, 2, false, a.toLowerCase(), null, false, false);
});
["autoReverse", "externalResourcesRequired", "focusable", "preserveAlpha"].forEach(function(a) {
  z[a] = new v(a, 2, false, a, null, false, false);
});
"allowFullScreen async autoFocus autoPlay controls default defer disabled disablePictureInPicture disableRemotePlayback formNoValidate hidden loop noModule noValidate open playsInline readOnly required reversed scoped seamless itemScope".split(" ").forEach(function(a) {
  z[a] = new v(a, 3, false, a.toLowerCase(), null, false, false);
});
["checked", "multiple", "muted", "selected"].forEach(function(a) {
  z[a] = new v(a, 3, true, a, null, false, false);
});
["capture", "download"].forEach(function(a) {
  z[a] = new v(a, 4, false, a, null, false, false);
});
["cols", "rows", "size", "span"].forEach(function(a) {
  z[a] = new v(a, 6, false, a, null, false, false);
});
["rowSpan", "start"].forEach(function(a) {
  z[a] = new v(a, 5, false, a.toLowerCase(), null, false, false);
});
var ra = /[\-:]([a-z])/g;
function sa(a) {
  return a[1].toUpperCase();
}
"accent-height alignment-baseline arabic-form baseline-shift cap-height clip-path clip-rule color-interpolation color-interpolation-filters color-profile color-rendering dominant-baseline enable-background fill-opacity fill-rule flood-color flood-opacity font-family font-size font-size-adjust font-stretch font-style font-variant font-weight glyph-name glyph-orientation-horizontal glyph-orientation-vertical horiz-adv-x horiz-origin-x image-rendering letter-spacing lighting-color marker-end marker-mid marker-start overline-position overline-thickness paint-order panose-1 pointer-events rendering-intent shape-rendering stop-color stop-opacity strikethrough-position strikethrough-thickness stroke-dasharray stroke-dashoffset stroke-linecap stroke-linejoin stroke-miterlimit stroke-opacity stroke-width text-anchor text-decoration text-rendering underline-position underline-thickness unicode-bidi unicode-range units-per-em v-alphabetic v-hanging v-ideographic v-mathematical vector-effect vert-adv-y vert-origin-x vert-origin-y word-spacing writing-mode xmlns:xlink x-height".split(" ").forEach(function(a) {
  var b = a.replace(
    ra,
    sa
  );
  z[b] = new v(b, 1, false, a, null, false, false);
});
"xlink:actuate xlink:arcrole xlink:role xlink:show xlink:title xlink:type".split(" ").forEach(function(a) {
  var b = a.replace(ra, sa);
  z[b] = new v(b, 1, false, a, "http://www.w3.org/1999/xlink", false, false);
});
["xml:base", "xml:lang", "xml:space"].forEach(function(a) {
  var b = a.replace(ra, sa);
  z[b] = new v(b, 1, false, a, "http://www.w3.org/XML/1998/namespace", false, false);
});
["tabIndex", "crossOrigin"].forEach(function(a) {
  z[a] = new v(a, 1, false, a.toLowerCase(), null, false, false);
});
z.xlinkHref = new v("xlinkHref", 1, false, "xlink:href", "http://www.w3.org/1999/xlink", true, false);
["src", "href", "action", "formAction"].forEach(function(a) {
  z[a] = new v(a, 1, false, a.toLowerCase(), null, true, true);
});
function ta(a, b, c, d) {
  var e = z.hasOwnProperty(b) ? z[b] : null;
  if (null !== e ? 0 !== e.type : d || !(2 < b.length) || "o" !== b[0] && "O" !== b[0] || "n" !== b[1] && "N" !== b[1]) qa(b, c, e, d) && (c = null), d || null === e ? oa(b) && (null === c ? a.removeAttribute(b) : a.setAttribute(b, "" + c)) : e.mustUseProperty ? a[e.propertyName] = null === c ? 3 === e.type ? false : "" : c : (b = e.attributeName, d = e.attributeNamespace, null === c ? a.removeAttribute(b) : (e = e.type, c = 3 === e || 4 === e && true === c ? "" : "" + c, d ? a.setAttributeNS(d, b, c) : a.setAttribute(b, c)));
}
var ua = aa.__SECRET_INTERNALS_DO_NOT_USE_OR_YOU_WILL_BE_FIRED, va = Symbol.for("react.element"), wa = Symbol.for("react.portal"), ya = Symbol.for("react.fragment"), za = Symbol.for("react.strict_mode"), Aa = Symbol.for("react.profiler"), Ba = Symbol.for("react.provider"), Ca = Symbol.for("react.context"), Da = Symbol.for("react.forward_ref"), Ea = Symbol.for("react.suspense"), Fa = Symbol.for("react.suspense_list"), Ga = Symbol.for("react.memo"), Ha = Symbol.for("react.lazy");
var Ia = Symbol.for("react.offscreen");
var Ja = Symbol.iterator;
function Ka(a) {
  if (null === a || "object" !== typeof a) return null;
  a = Ja && a[Ja] || a["@@iterator"];
  return "function" === typeof a ? a : null;
}
var A = Object.assign, La;
function Ma(a) {
  if (void 0 === La) try {
    throw Error();
  } catch (c) {
    var b = c.stack.trim().match(/\n( *(at )?)/);
    La = b && b[1] || "";
  }
  return "\n" + La + a;
}
var Na = false;
function Oa(a, b) {
  if (!a || Na) return "";
  Na = true;
  var c = Error.prepareStackTrace;
  Error.prepareStackTrace = void 0;
  try {
    if (b) if (b = function() {
      throw Error();
    }, Object.defineProperty(b.prototype, "props", { set: function() {
      throw Error();
    } }), "object" === typeof Reflect && Reflect.construct) {
      try {
        Reflect.construct(b, []);
      } catch (l2) {
        var d = l2;
      }
      Reflect.construct(a, [], b);
    } else {
      try {
        b.call();
      } catch (l2) {
        d = l2;
      }
      a.call(b.prototype);
    }
    else {
      try {
        throw Error();
      } catch (l2) {
        d = l2;
      }
      a();
    }
  } catch (l2) {
    if (l2 && d && "string" === typeof l2.stack) {
      for (var e = l2.stack.split("\n"), f2 = d.stack.split("\n"), g = e.length - 1, h = f2.length - 1; 1 <= g && 0 <= h && e[g] !== f2[h]; ) h--;
      for (; 1 <= g && 0 <= h; g--, h--) if (e[g] !== f2[h]) {
        if (1 !== g || 1 !== h) {
          do
            if (g--, h--, 0 > h || e[g] !== f2[h]) {
              var k2 = "\n" + e[g].replace(" at new ", " at ");
              a.displayName && k2.includes("<anonymous>") && (k2 = k2.replace("<anonymous>", a.displayName));
              return k2;
            }
          while (1 <= g && 0 <= h);
        }
        break;
      }
    }
  } finally {
    Na = false, Error.prepareStackTrace = c;
  }
  return (a = a ? a.displayName || a.name : "") ? Ma(a) : "";
}
function Pa(a) {
  switch (a.tag) {
    case 5:
      return Ma(a.type);
    case 16:
      return Ma("Lazy");
    case 13:
      return Ma("Suspense");
    case 19:
      return Ma("SuspenseList");
    case 0:
    case 2:
    case 15:
      return a = Oa(a.type, false), a;
    case 11:
      return a = Oa(a.type.render, false), a;
    case 1:
      return a = Oa(a.type, true), a;
    default:
      return "";
  }
}
function Qa(a) {
  if (null == a) return null;
  if ("function" === typeof a) return a.displayName || a.name || null;
  if ("string" === typeof a) return a;
  switch (a) {
    case ya:
      return "Fragment";
    case wa:
      return "Portal";
    case Aa:
      return "Profiler";
    case za:
      return "StrictMode";
    case Ea:
      return "Suspense";
    case Fa:
      return "SuspenseList";
  }
  if ("object" === typeof a) switch (a.$$typeof) {
    case Ca:
      return (a.displayName || "Context") + ".Consumer";
    case Ba:
      return (a._context.displayName || "Context") + ".Provider";
    case Da:
      var b = a.render;
      a = a.displayName;
      a || (a = b.displayName || b.name || "", a = "" !== a ? "ForwardRef(" + a + ")" : "ForwardRef");
      return a;
    case Ga:
      return b = a.displayName || null, null !== b ? b : Qa(a.type) || "Memo";
    case Ha:
      b = a._payload;
      a = a._init;
      try {
        return Qa(a(b));
      } catch (c) {
      }
  }
  return null;
}
function Ra(a) {
  var b = a.type;
  switch (a.tag) {
    case 24:
      return "Cache";
    case 9:
      return (b.displayName || "Context") + ".Consumer";
    case 10:
      return (b._context.displayName || "Context") + ".Provider";
    case 18:
      return "DehydratedFragment";
    case 11:
      return a = b.render, a = a.displayName || a.name || "", b.displayName || ("" !== a ? "ForwardRef(" + a + ")" : "ForwardRef");
    case 7:
      return "Fragment";
    case 5:
      return b;
    case 4:
      return "Portal";
    case 3:
      return "Root";
    case 6:
      return "Text";
    case 16:
      return Qa(b);
    case 8:
      return b === za ? "StrictMode" : "Mode";
    case 22:
      return "Offscreen";
    case 12:
      return "Profiler";
    case 21:
      return "Scope";
    case 13:
      return "Suspense";
    case 19:
      return "SuspenseList";
    case 25:
      return "TracingMarker";
    case 1:
    case 0:
    case 17:
    case 2:
    case 14:
    case 15:
      if ("function" === typeof b) return b.displayName || b.name || null;
      if ("string" === typeof b) return b;
  }
  return null;
}
function Sa(a) {
  switch (typeof a) {
    case "boolean":
    case "number":
    case "string":
    case "undefined":
      return a;
    case "object":
      return a;
    default:
      return "";
  }
}
function Ta(a) {
  var b = a.type;
  return (a = a.nodeName) && "input" === a.toLowerCase() && ("checkbox" === b || "radio" === b);
}
function Ua(a) {
  var b = Ta(a) ? "checked" : "value", c = Object.getOwnPropertyDescriptor(a.constructor.prototype, b), d = "" + a[b];
  if (!a.hasOwnProperty(b) && "undefined" !== typeof c && "function" === typeof c.get && "function" === typeof c.set) {
    var e = c.get, f2 = c.set;
    Object.defineProperty(a, b, { configurable: true, get: function() {
      return e.call(this);
    }, set: function(a2) {
      d = "" + a2;
      f2.call(this, a2);
    } });
    Object.defineProperty(a, b, { enumerable: c.enumerable });
    return { getValue: function() {
      return d;
    }, setValue: function(a2) {
      d = "" + a2;
    }, stopTracking: function() {
      a._valueTracker = null;
      delete a[b];
    } };
  }
}
function Va(a) {
  a._valueTracker || (a._valueTracker = Ua(a));
}
function Wa(a) {
  if (!a) return false;
  var b = a._valueTracker;
  if (!b) return true;
  var c = b.getValue();
  var d = "";
  a && (d = Ta(a) ? a.checked ? "true" : "false" : a.value);
  a = d;
  return a !== c ? (b.setValue(a), true) : false;
}
function Xa(a) {
  a = a || ("undefined" !== typeof document ? document : void 0);
  if ("undefined" === typeof a) return null;
  try {
    return a.activeElement || a.body;
  } catch (b) {
    return a.body;
  }
}
function Ya(a, b) {
  var c = b.checked;
  return A({}, b, { defaultChecked: void 0, defaultValue: void 0, value: void 0, checked: null != c ? c : a._wrapperState.initialChecked });
}
function Za(a, b) {
  var c = null == b.defaultValue ? "" : b.defaultValue, d = null != b.checked ? b.checked : b.defaultChecked;
  c = Sa(null != b.value ? b.value : c);
  a._wrapperState = { initialChecked: d, initialValue: c, controlled: "checkbox" === b.type || "radio" === b.type ? null != b.checked : null != b.value };
}
function ab(a, b) {
  b = b.checked;
  null != b && ta(a, "checked", b, false);
}
function bb(a, b) {
  ab(a, b);
  var c = Sa(b.value), d = b.type;
  if (null != c) if ("number" === d) {
    if (0 === c && "" === a.value || a.value != c) a.value = "" + c;
  } else a.value !== "" + c && (a.value = "" + c);
  else if ("submit" === d || "reset" === d) {
    a.removeAttribute("value");
    return;
  }
  b.hasOwnProperty("value") ? cb(a, b.type, c) : b.hasOwnProperty("defaultValue") && cb(a, b.type, Sa(b.defaultValue));
  null == b.checked && null != b.defaultChecked && (a.defaultChecked = !!b.defaultChecked);
}
function db(a, b, c) {
  if (b.hasOwnProperty("value") || b.hasOwnProperty("defaultValue")) {
    var d = b.type;
    if (!("submit" !== d && "reset" !== d || void 0 !== b.value && null !== b.value)) return;
    b = "" + a._wrapperState.initialValue;
    c || b === a.value || (a.value = b);
    a.defaultValue = b;
  }
  c = a.name;
  "" !== c && (a.name = "");
  a.defaultChecked = !!a._wrapperState.initialChecked;
  "" !== c && (a.name = c);
}
function cb(a, b, c) {
  if ("number" !== b || Xa(a.ownerDocument) !== a) null == c ? a.defaultValue = "" + a._wrapperState.initialValue : a.defaultValue !== "" + c && (a.defaultValue = "" + c);
}
var eb = Array.isArray;
function fb(a, b, c, d) {
  a = a.options;
  if (b) {
    b = {};
    for (var e = 0; e < c.length; e++) b["$" + c[e]] = true;
    for (c = 0; c < a.length; c++) e = b.hasOwnProperty("$" + a[c].value), a[c].selected !== e && (a[c].selected = e), e && d && (a[c].defaultSelected = true);
  } else {
    c = "" + Sa(c);
    b = null;
    for (e = 0; e < a.length; e++) {
      if (a[e].value === c) {
        a[e].selected = true;
        d && (a[e].defaultSelected = true);
        return;
      }
      null !== b || a[e].disabled || (b = a[e]);
    }
    null !== b && (b.selected = true);
  }
}
function gb(a, b) {
  if (null != b.dangerouslySetInnerHTML) throw Error(p(91));
  return A({}, b, { value: void 0, defaultValue: void 0, children: "" + a._wrapperState.initialValue });
}
function hb(a, b) {
  var c = b.value;
  if (null == c) {
    c = b.children;
    b = b.defaultValue;
    if (null != c) {
      if (null != b) throw Error(p(92));
      if (eb(c)) {
        if (1 < c.length) throw Error(p(93));
        c = c[0];
      }
      b = c;
    }
    null == b && (b = "");
    c = b;
  }
  a._wrapperState = { initialValue: Sa(c) };
}
function ib(a, b) {
  var c = Sa(b.value), d = Sa(b.defaultValue);
  null != c && (c = "" + c, c !== a.value && (a.value = c), null == b.defaultValue && a.defaultValue !== c && (a.defaultValue = c));
  null != d && (a.defaultValue = "" + d);
}
function jb(a) {
  var b = a.textContent;
  b === a._wrapperState.initialValue && "" !== b && null !== b && (a.value = b);
}
function kb(a) {
  switch (a) {
    case "svg":
      return "http://www.w3.org/2000/svg";
    case "math":
      return "http://www.w3.org/1998/Math/MathML";
    default:
      return "http://www.w3.org/1999/xhtml";
  }
}
function lb(a, b) {
  return null == a || "http://www.w3.org/1999/xhtml" === a ? kb(b) : "http://www.w3.org/2000/svg" === a && "foreignObject" === b ? "http://www.w3.org/1999/xhtml" : a;
}
var mb, nb = function(a) {
  return "undefined" !== typeof MSApp && MSApp.execUnsafeLocalFunction ? function(b, c, d, e) {
    MSApp.execUnsafeLocalFunction(function() {
      return a(b, c, d, e);
    });
  } : a;
}(function(a, b) {
  if ("http://www.w3.org/2000/svg" !== a.namespaceURI || "innerHTML" in a) a.innerHTML = b;
  else {
    mb = mb || document.createElement("div");
    mb.innerHTML = "<svg>" + b.valueOf().toString() + "</svg>";
    for (b = mb.firstChild; a.firstChild; ) a.removeChild(a.firstChild);
    for (; b.firstChild; ) a.appendChild(b.firstChild);
  }
});
function ob(a, b) {
  if (b) {
    var c = a.firstChild;
    if (c && c === a.lastChild && 3 === c.nodeType) {
      c.nodeValue = b;
      return;
    }
  }
  a.textContent = b;
}
var pb = {
  animationIterationCount: true,
  aspectRatio: true,
  borderImageOutset: true,
  borderImageSlice: true,
  borderImageWidth: true,
  boxFlex: true,
  boxFlexGroup: true,
  boxOrdinalGroup: true,
  columnCount: true,
  columns: true,
  flex: true,
  flexGrow: true,
  flexPositive: true,
  flexShrink: true,
  flexNegative: true,
  flexOrder: true,
  gridArea: true,
  gridRow: true,
  gridRowEnd: true,
  gridRowSpan: true,
  gridRowStart: true,
  gridColumn: true,
  gridColumnEnd: true,
  gridColumnSpan: true,
  gridColumnStart: true,
  fontWeight: true,
  lineClamp: true,
  lineHeight: true,
  opacity: true,
  order: true,
  orphans: true,
  tabSize: true,
  widows: true,
  zIndex: true,
  zoom: true,
  fillOpacity: true,
  floodOpacity: true,
  stopOpacity: true,
  strokeDasharray: true,
  strokeDashoffset: true,
  strokeMiterlimit: true,
  strokeOpacity: true,
  strokeWidth: true
}, qb = ["Webkit", "ms", "Moz", "O"];
Object.keys(pb).forEach(function(a) {
  qb.forEach(function(b) {
    b = b + a.charAt(0).toUpperCase() + a.substring(1);
    pb[b] = pb[a];
  });
});
function rb(a, b, c) {
  return null == b || "boolean" === typeof b || "" === b ? "" : c || "number" !== typeof b || 0 === b || pb.hasOwnProperty(a) && pb[a] ? ("" + b).trim() : b + "px";
}
function sb(a, b) {
  a = a.style;
  for (var c in b) if (b.hasOwnProperty(c)) {
    var d = 0 === c.indexOf("--"), e = rb(c, b[c], d);
    "float" === c && (c = "cssFloat");
    d ? a.setProperty(c, e) : a[c] = e;
  }
}
var tb = A({ menuitem: true }, { area: true, base: true, br: true, col: true, embed: true, hr: true, img: true, input: true, keygen: true, link: true, meta: true, param: true, source: true, track: true, wbr: true });
function ub(a, b) {
  if (b) {
    if (tb[a] && (null != b.children || null != b.dangerouslySetInnerHTML)) throw Error(p(137, a));
    if (null != b.dangerouslySetInnerHTML) {
      if (null != b.children) throw Error(p(60));
      if ("object" !== typeof b.dangerouslySetInnerHTML || !("__html" in b.dangerouslySetInnerHTML)) throw Error(p(61));
    }
    if (null != b.style && "object" !== typeof b.style) throw Error(p(62));
  }
}
function vb(a, b) {
  if (-1 === a.indexOf("-")) return "string" === typeof b.is;
  switch (a) {
    case "annotation-xml":
    case "color-profile":
    case "font-face":
    case "font-face-src":
    case "font-face-uri":
    case "font-face-format":
    case "font-face-name":
    case "missing-glyph":
      return false;
    default:
      return true;
  }
}
var wb = null;
function xb(a) {
  a = a.target || a.srcElement || window;
  a.correspondingUseElement && (a = a.correspondingUseElement);
  return 3 === a.nodeType ? a.parentNode : a;
}
var yb = null, zb = null, Ab = null;
function Bb(a) {
  if (a = Cb(a)) {
    if ("function" !== typeof yb) throw Error(p(280));
    var b = a.stateNode;
    b && (b = Db(b), yb(a.stateNode, a.type, b));
  }
}
function Eb(a) {
  zb ? Ab ? Ab.push(a) : Ab = [a] : zb = a;
}
function Fb() {
  if (zb) {
    var a = zb, b = Ab;
    Ab = zb = null;
    Bb(a);
    if (b) for (a = 0; a < b.length; a++) Bb(b[a]);
  }
}
function Gb(a, b) {
  return a(b);
}
function Hb() {
}
var Ib = false;
function Jb(a, b, c) {
  if (Ib) return a(b, c);
  Ib = true;
  try {
    return Gb(a, b, c);
  } finally {
    if (Ib = false, null !== zb || null !== Ab) Hb(), Fb();
  }
}
function Kb(a, b) {
  var c = a.stateNode;
  if (null === c) return null;
  var d = Db(c);
  if (null === d) return null;
  c = d[b];
  a: switch (b) {
    case "onClick":
    case "onClickCapture":
    case "onDoubleClick":
    case "onDoubleClickCapture":
    case "onMouseDown":
    case "onMouseDownCapture":
    case "onMouseMove":
    case "onMouseMoveCapture":
    case "onMouseUp":
    case "onMouseUpCapture":
    case "onMouseEnter":
      (d = !d.disabled) || (a = a.type, d = !("button" === a || "input" === a || "select" === a || "textarea" === a));
      a = !d;
      break a;
    default:
      a = false;
  }
  if (a) return null;
  if (c && "function" !== typeof c) throw Error(p(231, b, typeof c));
  return c;
}
var Lb = false;
if (ia) try {
  var Mb = {};
  Object.defineProperty(Mb, "passive", { get: function() {
    Lb = true;
  } });
  window.addEventListener("test", Mb, Mb);
  window.removeEventListener("test", Mb, Mb);
} catch (a) {
  Lb = false;
}
function Nb(a, b, c, d, e, f2, g, h, k2) {
  var l2 = Array.prototype.slice.call(arguments, 3);
  try {
    b.apply(c, l2);
  } catch (m2) {
    this.onError(m2);
  }
}
var Ob = false, Pb = null, Qb = false, Rb = null, Sb = { onError: function(a) {
  Ob = true;
  Pb = a;
} };
function Tb(a, b, c, d, e, f2, g, h, k2) {
  Ob = false;
  Pb = null;
  Nb.apply(Sb, arguments);
}
function Ub(a, b, c, d, e, f2, g, h, k2) {
  Tb.apply(this, arguments);
  if (Ob) {
    if (Ob) {
      var l2 = Pb;
      Ob = false;
      Pb = null;
    } else throw Error(p(198));
    Qb || (Qb = true, Rb = l2);
  }
}
function Vb(a) {
  var b = a, c = a;
  if (a.alternate) for (; b.return; ) b = b.return;
  else {
    a = b;
    do
      b = a, 0 !== (b.flags & 4098) && (c = b.return), a = b.return;
    while (a);
  }
  return 3 === b.tag ? c : null;
}
function Wb(a) {
  if (13 === a.tag) {
    var b = a.memoizedState;
    null === b && (a = a.alternate, null !== a && (b = a.memoizedState));
    if (null !== b) return b.dehydrated;
  }
  return null;
}
function Xb(a) {
  if (Vb(a) !== a) throw Error(p(188));
}
function Yb(a) {
  var b = a.alternate;
  if (!b) {
    b = Vb(a);
    if (null === b) throw Error(p(188));
    return b !== a ? null : a;
  }
  for (var c = a, d = b; ; ) {
    var e = c.return;
    if (null === e) break;
    var f2 = e.alternate;
    if (null === f2) {
      d = e.return;
      if (null !== d) {
        c = d;
        continue;
      }
      break;
    }
    if (e.child === f2.child) {
      for (f2 = e.child; f2; ) {
        if (f2 === c) return Xb(e), a;
        if (f2 === d) return Xb(e), b;
        f2 = f2.sibling;
      }
      throw Error(p(188));
    }
    if (c.return !== d.return) c = e, d = f2;
    else {
      for (var g = false, h = e.child; h; ) {
        if (h === c) {
          g = true;
          c = e;
          d = f2;
          break;
        }
        if (h === d) {
          g = true;
          d = e;
          c = f2;
          break;
        }
        h = h.sibling;
      }
      if (!g) {
        for (h = f2.child; h; ) {
          if (h === c) {
            g = true;
            c = f2;
            d = e;
            break;
          }
          if (h === d) {
            g = true;
            d = f2;
            c = e;
            break;
          }
          h = h.sibling;
        }
        if (!g) throw Error(p(189));
      }
    }
    if (c.alternate !== d) throw Error(p(190));
  }
  if (3 !== c.tag) throw Error(p(188));
  return c.stateNode.current === c ? a : b;
}
function Zb(a) {
  a = Yb(a);
  return null !== a ? $b(a) : null;
}
function $b(a) {
  if (5 === a.tag || 6 === a.tag) return a;
  for (a = a.child; null !== a; ) {
    var b = $b(a);
    if (null !== b) return b;
    a = a.sibling;
  }
  return null;
}
var ac = ca.unstable_scheduleCallback, bc = ca.unstable_cancelCallback, cc = ca.unstable_shouldYield, dc = ca.unstable_requestPaint, B = ca.unstable_now, ec = ca.unstable_getCurrentPriorityLevel, fc = ca.unstable_ImmediatePriority, gc = ca.unstable_UserBlockingPriority, hc = ca.unstable_NormalPriority, ic = ca.unstable_LowPriority, jc = ca.unstable_IdlePriority, kc = null, lc = null;
function mc(a) {
  if (lc && "function" === typeof lc.onCommitFiberRoot) try {
    lc.onCommitFiberRoot(kc, a, void 0, 128 === (a.current.flags & 128));
  } catch (b) {
  }
}
var oc = Math.clz32 ? Math.clz32 : nc, pc = Math.log, qc = Math.LN2;
function nc(a) {
  a >>>= 0;
  return 0 === a ? 32 : 31 - (pc(a) / qc | 0) | 0;
}
var rc = 64, sc = 4194304;
function tc(a) {
  switch (a & -a) {
    case 1:
      return 1;
    case 2:
      return 2;
    case 4:
      return 4;
    case 8:
      return 8;
    case 16:
      return 16;
    case 32:
      return 32;
    case 64:
    case 128:
    case 256:
    case 512:
    case 1024:
    case 2048:
    case 4096:
    case 8192:
    case 16384:
    case 32768:
    case 65536:
    case 131072:
    case 262144:
    case 524288:
    case 1048576:
    case 2097152:
      return a & 4194240;
    case 4194304:
    case 8388608:
    case 16777216:
    case 33554432:
    case 67108864:
      return a & 130023424;
    case 134217728:
      return 134217728;
    case 268435456:
      return 268435456;
    case 536870912:
      return 536870912;
    case 1073741824:
      return 1073741824;
    default:
      return a;
  }
}
function uc(a, b) {
  var c = a.pendingLanes;
  if (0 === c) return 0;
  var d = 0, e = a.suspendedLanes, f2 = a.pingedLanes, g = c & 268435455;
  if (0 !== g) {
    var h = g & ~e;
    0 !== h ? d = tc(h) : (f2 &= g, 0 !== f2 && (d = tc(f2)));
  } else g = c & ~e, 0 !== g ? d = tc(g) : 0 !== f2 && (d = tc(f2));
  if (0 === d) return 0;
  if (0 !== b && b !== d && 0 === (b & e) && (e = d & -d, f2 = b & -b, e >= f2 || 16 === e && 0 !== (f2 & 4194240))) return b;
  0 !== (d & 4) && (d |= c & 16);
  b = a.entangledLanes;
  if (0 !== b) for (a = a.entanglements, b &= d; 0 < b; ) c = 31 - oc(b), e = 1 << c, d |= a[c], b &= ~e;
  return d;
}
function vc(a, b) {
  switch (a) {
    case 1:
    case 2:
    case 4:
      return b + 250;
    case 8:
    case 16:
    case 32:
    case 64:
    case 128:
    case 256:
    case 512:
    case 1024:
    case 2048:
    case 4096:
    case 8192:
    case 16384:
    case 32768:
    case 65536:
    case 131072:
    case 262144:
    case 524288:
    case 1048576:
    case 2097152:
      return b + 5e3;
    case 4194304:
    case 8388608:
    case 16777216:
    case 33554432:
    case 67108864:
      return -1;
    case 134217728:
    case 268435456:
    case 536870912:
    case 1073741824:
      return -1;
    default:
      return -1;
  }
}
function wc(a, b) {
  for (var c = a.suspendedLanes, d = a.pingedLanes, e = a.expirationTimes, f2 = a.pendingLanes; 0 < f2; ) {
    var g = 31 - oc(f2), h = 1 << g, k2 = e[g];
    if (-1 === k2) {
      if (0 === (h & c) || 0 !== (h & d)) e[g] = vc(h, b);
    } else k2 <= b && (a.expiredLanes |= h);
    f2 &= ~h;
  }
}
function xc(a) {
  a = a.pendingLanes & -1073741825;
  return 0 !== a ? a : a & 1073741824 ? 1073741824 : 0;
}
function yc() {
  var a = rc;
  rc <<= 1;
  0 === (rc & 4194240) && (rc = 64);
  return a;
}
function zc(a) {
  for (var b = [], c = 0; 31 > c; c++) b.push(a);
  return b;
}
function Ac(a, b, c) {
  a.pendingLanes |= b;
  536870912 !== b && (a.suspendedLanes = 0, a.pingedLanes = 0);
  a = a.eventTimes;
  b = 31 - oc(b);
  a[b] = c;
}
function Bc(a, b) {
  var c = a.pendingLanes & ~b;
  a.pendingLanes = b;
  a.suspendedLanes = 0;
  a.pingedLanes = 0;
  a.expiredLanes &= b;
  a.mutableReadLanes &= b;
  a.entangledLanes &= b;
  b = a.entanglements;
  var d = a.eventTimes;
  for (a = a.expirationTimes; 0 < c; ) {
    var e = 31 - oc(c), f2 = 1 << e;
    b[e] = 0;
    d[e] = -1;
    a[e] = -1;
    c &= ~f2;
  }
}
function Cc(a, b) {
  var c = a.entangledLanes |= b;
  for (a = a.entanglements; c; ) {
    var d = 31 - oc(c), e = 1 << d;
    e & b | a[d] & b && (a[d] |= b);
    c &= ~e;
  }
}
var C = 0;
function Dc(a) {
  a &= -a;
  return 1 < a ? 4 < a ? 0 !== (a & 268435455) ? 16 : 536870912 : 4 : 1;
}
var Ec, Fc, Gc, Hc, Ic, Jc = false, Kc = [], Lc = null, Mc = null, Nc = null, Oc = /* @__PURE__ */ new Map(), Pc = /* @__PURE__ */ new Map(), Qc = [], Rc = "mousedown mouseup touchcancel touchend touchstart auxclick dblclick pointercancel pointerdown pointerup dragend dragstart drop compositionend compositionstart keydown keypress keyup input textInput copy cut paste click change contextmenu reset submit".split(" ");
function Sc(a, b) {
  switch (a) {
    case "focusin":
    case "focusout":
      Lc = null;
      break;
    case "dragenter":
    case "dragleave":
      Mc = null;
      break;
    case "mouseover":
    case "mouseout":
      Nc = null;
      break;
    case "pointerover":
    case "pointerout":
      Oc.delete(b.pointerId);
      break;
    case "gotpointercapture":
    case "lostpointercapture":
      Pc.delete(b.pointerId);
  }
}
function Tc(a, b, c, d, e, f2) {
  if (null === a || a.nativeEvent !== f2) return a = { blockedOn: b, domEventName: c, eventSystemFlags: d, nativeEvent: f2, targetContainers: [e] }, null !== b && (b = Cb(b), null !== b && Fc(b)), a;
  a.eventSystemFlags |= d;
  b = a.targetContainers;
  null !== e && -1 === b.indexOf(e) && b.push(e);
  return a;
}
function Uc(a, b, c, d, e) {
  switch (b) {
    case "focusin":
      return Lc = Tc(Lc, a, b, c, d, e), true;
    case "dragenter":
      return Mc = Tc(Mc, a, b, c, d, e), true;
    case "mouseover":
      return Nc = Tc(Nc, a, b, c, d, e), true;
    case "pointerover":
      var f2 = e.pointerId;
      Oc.set(f2, Tc(Oc.get(f2) || null, a, b, c, d, e));
      return true;
    case "gotpointercapture":
      return f2 = e.pointerId, Pc.set(f2, Tc(Pc.get(f2) || null, a, b, c, d, e)), true;
  }
  return false;
}
function Vc(a) {
  var b = Wc(a.target);
  if (null !== b) {
    var c = Vb(b);
    if (null !== c) {
      if (b = c.tag, 13 === b) {
        if (b = Wb(c), null !== b) {
          a.blockedOn = b;
          Ic(a.priority, function() {
            Gc(c);
          });
          return;
        }
      } else if (3 === b && c.stateNode.current.memoizedState.isDehydrated) {
        a.blockedOn = 3 === c.tag ? c.stateNode.containerInfo : null;
        return;
      }
    }
  }
  a.blockedOn = null;
}
function Xc(a) {
  if (null !== a.blockedOn) return false;
  for (var b = a.targetContainers; 0 < b.length; ) {
    var c = Yc(a.domEventName, a.eventSystemFlags, b[0], a.nativeEvent);
    if (null === c) {
      c = a.nativeEvent;
      var d = new c.constructor(c.type, c);
      wb = d;
      c.target.dispatchEvent(d);
      wb = null;
    } else return b = Cb(c), null !== b && Fc(b), a.blockedOn = c, false;
    b.shift();
  }
  return true;
}
function Zc(a, b, c) {
  Xc(a) && c.delete(b);
}
function $c() {
  Jc = false;
  null !== Lc && Xc(Lc) && (Lc = null);
  null !== Mc && Xc(Mc) && (Mc = null);
  null !== Nc && Xc(Nc) && (Nc = null);
  Oc.forEach(Zc);
  Pc.forEach(Zc);
}
function ad(a, b) {
  a.blockedOn === b && (a.blockedOn = null, Jc || (Jc = true, ca.unstable_scheduleCallback(ca.unstable_NormalPriority, $c)));
}
function bd(a) {
  function b(b2) {
    return ad(b2, a);
  }
  if (0 < Kc.length) {
    ad(Kc[0], a);
    for (var c = 1; c < Kc.length; c++) {
      var d = Kc[c];
      d.blockedOn === a && (d.blockedOn = null);
    }
  }
  null !== Lc && ad(Lc, a);
  null !== Mc && ad(Mc, a);
  null !== Nc && ad(Nc, a);
  Oc.forEach(b);
  Pc.forEach(b);
  for (c = 0; c < Qc.length; c++) d = Qc[c], d.blockedOn === a && (d.blockedOn = null);
  for (; 0 < Qc.length && (c = Qc[0], null === c.blockedOn); ) Vc(c), null === c.blockedOn && Qc.shift();
}
var cd = ua.ReactCurrentBatchConfig, dd = true;
function ed(a, b, c, d) {
  var e = C, f2 = cd.transition;
  cd.transition = null;
  try {
    C = 1, fd(a, b, c, d);
  } finally {
    C = e, cd.transition = f2;
  }
}
function gd(a, b, c, d) {
  var e = C, f2 = cd.transition;
  cd.transition = null;
  try {
    C = 4, fd(a, b, c, d);
  } finally {
    C = e, cd.transition = f2;
  }
}
function fd(a, b, c, d) {
  if (dd) {
    var e = Yc(a, b, c, d);
    if (null === e) hd(a, b, d, id, c), Sc(a, d);
    else if (Uc(e, a, b, c, d)) d.stopPropagation();
    else if (Sc(a, d), b & 4 && -1 < Rc.indexOf(a)) {
      for (; null !== e; ) {
        var f2 = Cb(e);
        null !== f2 && Ec(f2);
        f2 = Yc(a, b, c, d);
        null === f2 && hd(a, b, d, id, c);
        if (f2 === e) break;
        e = f2;
      }
      null !== e && d.stopPropagation();
    } else hd(a, b, d, null, c);
  }
}
var id = null;
function Yc(a, b, c, d) {
  id = null;
  a = xb(d);
  a = Wc(a);
  if (null !== a) if (b = Vb(a), null === b) a = null;
  else if (c = b.tag, 13 === c) {
    a = Wb(b);
    if (null !== a) return a;
    a = null;
  } else if (3 === c) {
    if (b.stateNode.current.memoizedState.isDehydrated) return 3 === b.tag ? b.stateNode.containerInfo : null;
    a = null;
  } else b !== a && (a = null);
  id = a;
  return null;
}
function jd(a) {
  switch (a) {
    case "cancel":
    case "click":
    case "close":
    case "contextmenu":
    case "copy":
    case "cut":
    case "auxclick":
    case "dblclick":
    case "dragend":
    case "dragstart":
    case "drop":
    case "focusin":
    case "focusout":
    case "input":
    case "invalid":
    case "keydown":
    case "keypress":
    case "keyup":
    case "mousedown":
    case "mouseup":
    case "paste":
    case "pause":
    case "play":
    case "pointercancel":
    case "pointerdown":
    case "pointerup":
    case "ratechange":
    case "reset":
    case "resize":
    case "seeked":
    case "submit":
    case "touchcancel":
    case "touchend":
    case "touchstart":
    case "volumechange":
    case "change":
    case "selectionchange":
    case "textInput":
    case "compositionstart":
    case "compositionend":
    case "compositionupdate":
    case "beforeblur":
    case "afterblur":
    case "beforeinput":
    case "blur":
    case "fullscreenchange":
    case "focus":
    case "hashchange":
    case "popstate":
    case "select":
    case "selectstart":
      return 1;
    case "drag":
    case "dragenter":
    case "dragexit":
    case "dragleave":
    case "dragover":
    case "mousemove":
    case "mouseout":
    case "mouseover":
    case "pointermove":
    case "pointerout":
    case "pointerover":
    case "scroll":
    case "toggle":
    case "touchmove":
    case "wheel":
    case "mouseenter":
    case "mouseleave":
    case "pointerenter":
    case "pointerleave":
      return 4;
    case "message":
      switch (ec()) {
        case fc:
          return 1;
        case gc:
          return 4;
        case hc:
        case ic:
          return 16;
        case jc:
          return 536870912;
        default:
          return 16;
      }
    default:
      return 16;
  }
}
var kd = null, ld = null, md = null;
function nd() {
  if (md) return md;
  var a, b = ld, c = b.length, d, e = "value" in kd ? kd.value : kd.textContent, f2 = e.length;
  for (a = 0; a < c && b[a] === e[a]; a++) ;
  var g = c - a;
  for (d = 1; d <= g && b[c - d] === e[f2 - d]; d++) ;
  return md = e.slice(a, 1 < d ? 1 - d : void 0);
}
function od(a) {
  var b = a.keyCode;
  "charCode" in a ? (a = a.charCode, 0 === a && 13 === b && (a = 13)) : a = b;
  10 === a && (a = 13);
  return 32 <= a || 13 === a ? a : 0;
}
function pd() {
  return true;
}
function qd() {
  return false;
}
function rd(a) {
  function b(b2, d, e, f2, g) {
    this._reactName = b2;
    this._targetInst = e;
    this.type = d;
    this.nativeEvent = f2;
    this.target = g;
    this.currentTarget = null;
    for (var c in a) a.hasOwnProperty(c) && (b2 = a[c], this[c] = b2 ? b2(f2) : f2[c]);
    this.isDefaultPrevented = (null != f2.defaultPrevented ? f2.defaultPrevented : false === f2.returnValue) ? pd : qd;
    this.isPropagationStopped = qd;
    return this;
  }
  A(b.prototype, { preventDefault: function() {
    this.defaultPrevented = true;
    var a2 = this.nativeEvent;
    a2 && (a2.preventDefault ? a2.preventDefault() : "unknown" !== typeof a2.returnValue && (a2.returnValue = false), this.isDefaultPrevented = pd);
  }, stopPropagation: function() {
    var a2 = this.nativeEvent;
    a2 && (a2.stopPropagation ? a2.stopPropagation() : "unknown" !== typeof a2.cancelBubble && (a2.cancelBubble = true), this.isPropagationStopped = pd);
  }, persist: function() {
  }, isPersistent: pd });
  return b;
}
var sd = { eventPhase: 0, bubbles: 0, cancelable: 0, timeStamp: function(a) {
  return a.timeStamp || Date.now();
}, defaultPrevented: 0, isTrusted: 0 }, td = rd(sd), ud = A({}, sd, { view: 0, detail: 0 }), vd = rd(ud), wd, xd, yd, Ad = A({}, ud, { screenX: 0, screenY: 0, clientX: 0, clientY: 0, pageX: 0, pageY: 0, ctrlKey: 0, shiftKey: 0, altKey: 0, metaKey: 0, getModifierState: zd, button: 0, buttons: 0, relatedTarget: function(a) {
  return void 0 === a.relatedTarget ? a.fromElement === a.srcElement ? a.toElement : a.fromElement : a.relatedTarget;
}, movementX: function(a) {
  if ("movementX" in a) return a.movementX;
  a !== yd && (yd && "mousemove" === a.type ? (wd = a.screenX - yd.screenX, xd = a.screenY - yd.screenY) : xd = wd = 0, yd = a);
  return wd;
}, movementY: function(a) {
  return "movementY" in a ? a.movementY : xd;
} }), Bd = rd(Ad), Cd = A({}, Ad, { dataTransfer: 0 }), Dd = rd(Cd), Ed = A({}, ud, { relatedTarget: 0 }), Fd = rd(Ed), Gd = A({}, sd, { animationName: 0, elapsedTime: 0, pseudoElement: 0 }), Hd = rd(Gd), Id = A({}, sd, { clipboardData: function(a) {
  return "clipboardData" in a ? a.clipboardData : window.clipboardData;
} }), Jd = rd(Id), Kd = A({}, sd, { data: 0 }), Ld = rd(Kd), Md = {
  Esc: "Escape",
  Spacebar: " ",
  Left: "ArrowLeft",
  Up: "ArrowUp",
  Right: "ArrowRight",
  Down: "ArrowDown",
  Del: "Delete",
  Win: "OS",
  Menu: "ContextMenu",
  Apps: "ContextMenu",
  Scroll: "ScrollLock",
  MozPrintableKey: "Unidentified"
}, Nd = {
  8: "Backspace",
  9: "Tab",
  12: "Clear",
  13: "Enter",
  16: "Shift",
  17: "Control",
  18: "Alt",
  19: "Pause",
  20: "CapsLock",
  27: "Escape",
  32: " ",
  33: "PageUp",
  34: "PageDown",
  35: "End",
  36: "Home",
  37: "ArrowLeft",
  38: "ArrowUp",
  39: "ArrowRight",
  40: "ArrowDown",
  45: "Insert",
  46: "Delete",
  112: "F1",
  113: "F2",
  114: "F3",
  115: "F4",
  116: "F5",
  117: "F6",
  118: "F7",
  119: "F8",
  120: "F9",
  121: "F10",
  122: "F11",
  123: "F12",
  144: "NumLock",
  145: "ScrollLock",
  224: "Meta"
}, Od = { Alt: "altKey", Control: "ctrlKey", Meta: "metaKey", Shift: "shiftKey" };
function Pd(a) {
  var b = this.nativeEvent;
  return b.getModifierState ? b.getModifierState(a) : (a = Od[a]) ? !!b[a] : false;
}
function zd() {
  return Pd;
}
var Qd = A({}, ud, { key: function(a) {
  if (a.key) {
    var b = Md[a.key] || a.key;
    if ("Unidentified" !== b) return b;
  }
  return "keypress" === a.type ? (a = od(a), 13 === a ? "Enter" : String.fromCharCode(a)) : "keydown" === a.type || "keyup" === a.type ? Nd[a.keyCode] || "Unidentified" : "";
}, code: 0, location: 0, ctrlKey: 0, shiftKey: 0, altKey: 0, metaKey: 0, repeat: 0, locale: 0, getModifierState: zd, charCode: function(a) {
  return "keypress" === a.type ? od(a) : 0;
}, keyCode: function(a) {
  return "keydown" === a.type || "keyup" === a.type ? a.keyCode : 0;
}, which: function(a) {
  return "keypress" === a.type ? od(a) : "keydown" === a.type || "keyup" === a.type ? a.keyCode : 0;
} }), Rd = rd(Qd), Sd = A({}, Ad, { pointerId: 0, width: 0, height: 0, pressure: 0, tangentialPressure: 0, tiltX: 0, tiltY: 0, twist: 0, pointerType: 0, isPrimary: 0 }), Td = rd(Sd), Ud = A({}, ud, { touches: 0, targetTouches: 0, changedTouches: 0, altKey: 0, metaKey: 0, ctrlKey: 0, shiftKey: 0, getModifierState: zd }), Vd = rd(Ud), Wd = A({}, sd, { propertyName: 0, elapsedTime: 0, pseudoElement: 0 }), Xd = rd(Wd), Yd = A({}, Ad, {
  deltaX: function(a) {
    return "deltaX" in a ? a.deltaX : "wheelDeltaX" in a ? -a.wheelDeltaX : 0;
  },
  deltaY: function(a) {
    return "deltaY" in a ? a.deltaY : "wheelDeltaY" in a ? -a.wheelDeltaY : "wheelDelta" in a ? -a.wheelDelta : 0;
  },
  deltaZ: 0,
  deltaMode: 0
}), Zd = rd(Yd), $d = [9, 13, 27, 32], ae = ia && "CompositionEvent" in window, be = null;
ia && "documentMode" in document && (be = document.documentMode);
var ce = ia && "TextEvent" in window && !be, de = ia && (!ae || be && 8 < be && 11 >= be), ee = String.fromCharCode(32), fe = false;
function ge(a, b) {
  switch (a) {
    case "keyup":
      return -1 !== $d.indexOf(b.keyCode);
    case "keydown":
      return 229 !== b.keyCode;
    case "keypress":
    case "mousedown":
    case "focusout":
      return true;
    default:
      return false;
  }
}
function he(a) {
  a = a.detail;
  return "object" === typeof a && "data" in a ? a.data : null;
}
var ie = false;
function je(a, b) {
  switch (a) {
    case "compositionend":
      return he(b);
    case "keypress":
      if (32 !== b.which) return null;
      fe = true;
      return ee;
    case "textInput":
      return a = b.data, a === ee && fe ? null : a;
    default:
      return null;
  }
}
function ke(a, b) {
  if (ie) return "compositionend" === a || !ae && ge(a, b) ? (a = nd(), md = ld = kd = null, ie = false, a) : null;
  switch (a) {
    case "paste":
      return null;
    case "keypress":
      if (!(b.ctrlKey || b.altKey || b.metaKey) || b.ctrlKey && b.altKey) {
        if (b.char && 1 < b.char.length) return b.char;
        if (b.which) return String.fromCharCode(b.which);
      }
      return null;
    case "compositionend":
      return de && "ko" !== b.locale ? null : b.data;
    default:
      return null;
  }
}
var le = { color: true, date: true, datetime: true, "datetime-local": true, email: true, month: true, number: true, password: true, range: true, search: true, tel: true, text: true, time: true, url: true, week: true };
function me(a) {
  var b = a && a.nodeName && a.nodeName.toLowerCase();
  return "input" === b ? !!le[a.type] : "textarea" === b ? true : false;
}
function ne(a, b, c, d) {
  Eb(d);
  b = oe(b, "onChange");
  0 < b.length && (c = new td("onChange", "change", null, c, d), a.push({ event: c, listeners: b }));
}
var pe = null, qe = null;
function re(a) {
  se(a, 0);
}
function te(a) {
  var b = ue(a);
  if (Wa(b)) return a;
}
function ve(a, b) {
  if ("change" === a) return b;
}
var we = false;
if (ia) {
  var xe;
  if (ia) {
    var ye = "oninput" in document;
    if (!ye) {
      var ze = document.createElement("div");
      ze.setAttribute("oninput", "return;");
      ye = "function" === typeof ze.oninput;
    }
    xe = ye;
  } else xe = false;
  we = xe && (!document.documentMode || 9 < document.documentMode);
}
function Ae() {
  pe && (pe.detachEvent("onpropertychange", Be), qe = pe = null);
}
function Be(a) {
  if ("value" === a.propertyName && te(qe)) {
    var b = [];
    ne(b, qe, a, xb(a));
    Jb(re, b);
  }
}
function Ce(a, b, c) {
  "focusin" === a ? (Ae(), pe = b, qe = c, pe.attachEvent("onpropertychange", Be)) : "focusout" === a && Ae();
}
function De(a) {
  if ("selectionchange" === a || "keyup" === a || "keydown" === a) return te(qe);
}
function Ee(a, b) {
  if ("click" === a) return te(b);
}
function Fe(a, b) {
  if ("input" === a || "change" === a) return te(b);
}
function Ge(a, b) {
  return a === b && (0 !== a || 1 / a === 1 / b) || a !== a && b !== b;
}
var He = "function" === typeof Object.is ? Object.is : Ge;
function Ie(a, b) {
  if (He(a, b)) return true;
  if ("object" !== typeof a || null === a || "object" !== typeof b || null === b) return false;
  var c = Object.keys(a), d = Object.keys(b);
  if (c.length !== d.length) return false;
  for (d = 0; d < c.length; d++) {
    var e = c[d];
    if (!ja.call(b, e) || !He(a[e], b[e])) return false;
  }
  return true;
}
function Je(a) {
  for (; a && a.firstChild; ) a = a.firstChild;
  return a;
}
function Ke(a, b) {
  var c = Je(a);
  a = 0;
  for (var d; c; ) {
    if (3 === c.nodeType) {
      d = a + c.textContent.length;
      if (a <= b && d >= b) return { node: c, offset: b - a };
      a = d;
    }
    a: {
      for (; c; ) {
        if (c.nextSibling) {
          c = c.nextSibling;
          break a;
        }
        c = c.parentNode;
      }
      c = void 0;
    }
    c = Je(c);
  }
}
function Le(a, b) {
  return a && b ? a === b ? true : a && 3 === a.nodeType ? false : b && 3 === b.nodeType ? Le(a, b.parentNode) : "contains" in a ? a.contains(b) : a.compareDocumentPosition ? !!(a.compareDocumentPosition(b) & 16) : false : false;
}
function Me() {
  for (var a = window, b = Xa(); b instanceof a.HTMLIFrameElement; ) {
    try {
      var c = "string" === typeof b.contentWindow.location.href;
    } catch (d) {
      c = false;
    }
    if (c) a = b.contentWindow;
    else break;
    b = Xa(a.document);
  }
  return b;
}
function Ne(a) {
  var b = a && a.nodeName && a.nodeName.toLowerCase();
  return b && ("input" === b && ("text" === a.type || "search" === a.type || "tel" === a.type || "url" === a.type || "password" === a.type) || "textarea" === b || "true" === a.contentEditable);
}
function Oe(a) {
  var b = Me(), c = a.focusedElem, d = a.selectionRange;
  if (b !== c && c && c.ownerDocument && Le(c.ownerDocument.documentElement, c)) {
    if (null !== d && Ne(c)) {
      if (b = d.start, a = d.end, void 0 === a && (a = b), "selectionStart" in c) c.selectionStart = b, c.selectionEnd = Math.min(a, c.value.length);
      else if (a = (b = c.ownerDocument || document) && b.defaultView || window, a.getSelection) {
        a = a.getSelection();
        var e = c.textContent.length, f2 = Math.min(d.start, e);
        d = void 0 === d.end ? f2 : Math.min(d.end, e);
        !a.extend && f2 > d && (e = d, d = f2, f2 = e);
        e = Ke(c, f2);
        var g = Ke(
          c,
          d
        );
        e && g && (1 !== a.rangeCount || a.anchorNode !== e.node || a.anchorOffset !== e.offset || a.focusNode !== g.node || a.focusOffset !== g.offset) && (b = b.createRange(), b.setStart(e.node, e.offset), a.removeAllRanges(), f2 > d ? (a.addRange(b), a.extend(g.node, g.offset)) : (b.setEnd(g.node, g.offset), a.addRange(b)));
      }
    }
    b = [];
    for (a = c; a = a.parentNode; ) 1 === a.nodeType && b.push({ element: a, left: a.scrollLeft, top: a.scrollTop });
    "function" === typeof c.focus && c.focus();
    for (c = 0; c < b.length; c++) a = b[c], a.element.scrollLeft = a.left, a.element.scrollTop = a.top;
  }
}
var Pe = ia && "documentMode" in document && 11 >= document.documentMode, Qe = null, Re = null, Se = null, Te = false;
function Ue(a, b, c) {
  var d = c.window === c ? c.document : 9 === c.nodeType ? c : c.ownerDocument;
  Te || null == Qe || Qe !== Xa(d) || (d = Qe, "selectionStart" in d && Ne(d) ? d = { start: d.selectionStart, end: d.selectionEnd } : (d = (d.ownerDocument && d.ownerDocument.defaultView || window).getSelection(), d = { anchorNode: d.anchorNode, anchorOffset: d.anchorOffset, focusNode: d.focusNode, focusOffset: d.focusOffset }), Se && Ie(Se, d) || (Se = d, d = oe(Re, "onSelect"), 0 < d.length && (b = new td("onSelect", "select", null, b, c), a.push({ event: b, listeners: d }), b.target = Qe)));
}
function Ve(a, b) {
  var c = {};
  c[a.toLowerCase()] = b.toLowerCase();
  c["Webkit" + a] = "webkit" + b;
  c["Moz" + a] = "moz" + b;
  return c;
}
var We = { animationend: Ve("Animation", "AnimationEnd"), animationiteration: Ve("Animation", "AnimationIteration"), animationstart: Ve("Animation", "AnimationStart"), transitionend: Ve("Transition", "TransitionEnd") }, Xe = {}, Ye = {};
ia && (Ye = document.createElement("div").style, "AnimationEvent" in window || (delete We.animationend.animation, delete We.animationiteration.animation, delete We.animationstart.animation), "TransitionEvent" in window || delete We.transitionend.transition);
function Ze(a) {
  if (Xe[a]) return Xe[a];
  if (!We[a]) return a;
  var b = We[a], c;
  for (c in b) if (b.hasOwnProperty(c) && c in Ye) return Xe[a] = b[c];
  return a;
}
var $e = Ze("animationend"), af = Ze("animationiteration"), bf = Ze("animationstart"), cf = Ze("transitionend"), df = /* @__PURE__ */ new Map(), ef = "abort auxClick cancel canPlay canPlayThrough click close contextMenu copy cut drag dragEnd dragEnter dragExit dragLeave dragOver dragStart drop durationChange emptied encrypted ended error gotPointerCapture input invalid keyDown keyPress keyUp load loadedData loadedMetadata loadStart lostPointerCapture mouseDown mouseMove mouseOut mouseOver mouseUp paste pause play playing pointerCancel pointerDown pointerMove pointerOut pointerOver pointerUp progress rateChange reset resize seeked seeking stalled submit suspend timeUpdate touchCancel touchEnd touchStart volumeChange scroll toggle touchMove waiting wheel".split(" ");
function ff(a, b) {
  df.set(a, b);
  fa(b, [a]);
}
for (var gf = 0; gf < ef.length; gf++) {
  var hf = ef[gf], jf = hf.toLowerCase(), kf = hf[0].toUpperCase() + hf.slice(1);
  ff(jf, "on" + kf);
}
ff($e, "onAnimationEnd");
ff(af, "onAnimationIteration");
ff(bf, "onAnimationStart");
ff("dblclick", "onDoubleClick");
ff("focusin", "onFocus");
ff("focusout", "onBlur");
ff(cf, "onTransitionEnd");
ha("onMouseEnter", ["mouseout", "mouseover"]);
ha("onMouseLeave", ["mouseout", "mouseover"]);
ha("onPointerEnter", ["pointerout", "pointerover"]);
ha("onPointerLeave", ["pointerout", "pointerover"]);
fa("onChange", "change click focusin focusout input keydown keyup selectionchange".split(" "));
fa("onSelect", "focusout contextmenu dragend focusin keydown keyup mousedown mouseup selectionchange".split(" "));
fa("onBeforeInput", ["compositionend", "keypress", "textInput", "paste"]);
fa("onCompositionEnd", "compositionend focusout keydown keypress keyup mousedown".split(" "));
fa("onCompositionStart", "compositionstart focusout keydown keypress keyup mousedown".split(" "));
fa("onCompositionUpdate", "compositionupdate focusout keydown keypress keyup mousedown".split(" "));
var lf = "abort canplay canplaythrough durationchange emptied encrypted ended error loadeddata loadedmetadata loadstart pause play playing progress ratechange resize seeked seeking stalled suspend timeupdate volumechange waiting".split(" "), mf = new Set("cancel close invalid load scroll toggle".split(" ").concat(lf));
function nf(a, b, c) {
  var d = a.type || "unknown-event";
  a.currentTarget = c;
  Ub(d, b, void 0, a);
  a.currentTarget = null;
}
function se(a, b) {
  b = 0 !== (b & 4);
  for (var c = 0; c < a.length; c++) {
    var d = a[c], e = d.event;
    d = d.listeners;
    a: {
      var f2 = void 0;
      if (b) for (var g = d.length - 1; 0 <= g; g--) {
        var h = d[g], k2 = h.instance, l2 = h.currentTarget;
        h = h.listener;
        if (k2 !== f2 && e.isPropagationStopped()) break a;
        nf(e, h, l2);
        f2 = k2;
      }
      else for (g = 0; g < d.length; g++) {
        h = d[g];
        k2 = h.instance;
        l2 = h.currentTarget;
        h = h.listener;
        if (k2 !== f2 && e.isPropagationStopped()) break a;
        nf(e, h, l2);
        f2 = k2;
      }
    }
  }
  if (Qb) throw a = Rb, Qb = false, Rb = null, a;
}
function D(a, b) {
  var c = b[of];
  void 0 === c && (c = b[of] = /* @__PURE__ */ new Set());
  var d = a + "__bubble";
  c.has(d) || (pf(b, a, 2, false), c.add(d));
}
function qf(a, b, c) {
  var d = 0;
  b && (d |= 4);
  pf(c, a, d, b);
}
var rf = "_reactListening" + Math.random().toString(36).slice(2);
function sf(a) {
  if (!a[rf]) {
    a[rf] = true;
    da.forEach(function(b2) {
      "selectionchange" !== b2 && (mf.has(b2) || qf(b2, false, a), qf(b2, true, a));
    });
    var b = 9 === a.nodeType ? a : a.ownerDocument;
    null === b || b[rf] || (b[rf] = true, qf("selectionchange", false, b));
  }
}
function pf(a, b, c, d) {
  switch (jd(b)) {
    case 1:
      var e = ed;
      break;
    case 4:
      e = gd;
      break;
    default:
      e = fd;
  }
  c = e.bind(null, b, c, a);
  e = void 0;
  !Lb || "touchstart" !== b && "touchmove" !== b && "wheel" !== b || (e = true);
  d ? void 0 !== e ? a.addEventListener(b, c, { capture: true, passive: e }) : a.addEventListener(b, c, true) : void 0 !== e ? a.addEventListener(b, c, { passive: e }) : a.addEventListener(b, c, false);
}
function hd(a, b, c, d, e) {
  var f2 = d;
  if (0 === (b & 1) && 0 === (b & 2) && null !== d) a: for (; ; ) {
    if (null === d) return;
    var g = d.tag;
    if (3 === g || 4 === g) {
      var h = d.stateNode.containerInfo;
      if (h === e || 8 === h.nodeType && h.parentNode === e) break;
      if (4 === g) for (g = d.return; null !== g; ) {
        var k2 = g.tag;
        if (3 === k2 || 4 === k2) {
          if (k2 = g.stateNode.containerInfo, k2 === e || 8 === k2.nodeType && k2.parentNode === e) return;
        }
        g = g.return;
      }
      for (; null !== h; ) {
        g = Wc(h);
        if (null === g) return;
        k2 = g.tag;
        if (5 === k2 || 6 === k2) {
          d = f2 = g;
          continue a;
        }
        h = h.parentNode;
      }
    }
    d = d.return;
  }
  Jb(function() {
    var d2 = f2, e2 = xb(c), g2 = [];
    a: {
      var h2 = df.get(a);
      if (void 0 !== h2) {
        var k3 = td, n2 = a;
        switch (a) {
          case "keypress":
            if (0 === od(c)) break a;
          case "keydown":
          case "keyup":
            k3 = Rd;
            break;
          case "focusin":
            n2 = "focus";
            k3 = Fd;
            break;
          case "focusout":
            n2 = "blur";
            k3 = Fd;
            break;
          case "beforeblur":
          case "afterblur":
            k3 = Fd;
            break;
          case "click":
            if (2 === c.button) break a;
          case "auxclick":
          case "dblclick":
          case "mousedown":
          case "mousemove":
          case "mouseup":
          case "mouseout":
          case "mouseover":
          case "contextmenu":
            k3 = Bd;
            break;
          case "drag":
          case "dragend":
          case "dragenter":
          case "dragexit":
          case "dragleave":
          case "dragover":
          case "dragstart":
          case "drop":
            k3 = Dd;
            break;
          case "touchcancel":
          case "touchend":
          case "touchmove":
          case "touchstart":
            k3 = Vd;
            break;
          case $e:
          case af:
          case bf:
            k3 = Hd;
            break;
          case cf:
            k3 = Xd;
            break;
          case "scroll":
            k3 = vd;
            break;
          case "wheel":
            k3 = Zd;
            break;
          case "copy":
          case "cut":
          case "paste":
            k3 = Jd;
            break;
          case "gotpointercapture":
          case "lostpointercapture":
          case "pointercancel":
          case "pointerdown":
          case "pointermove":
          case "pointerout":
          case "pointerover":
          case "pointerup":
            k3 = Td;
        }
        var t2 = 0 !== (b & 4), J2 = !t2 && "scroll" === a, x2 = t2 ? null !== h2 ? h2 + "Capture" : null : h2;
        t2 = [];
        for (var w2 = d2, u2; null !== w2; ) {
          u2 = w2;
          var F2 = u2.stateNode;
          5 === u2.tag && null !== F2 && (u2 = F2, null !== x2 && (F2 = Kb(w2, x2), null != F2 && t2.push(tf(w2, F2, u2))));
          if (J2) break;
          w2 = w2.return;
        }
        0 < t2.length && (h2 = new k3(h2, n2, null, c, e2), g2.push({ event: h2, listeners: t2 }));
      }
    }
    if (0 === (b & 7)) {
      a: {
        h2 = "mouseover" === a || "pointerover" === a;
        k3 = "mouseout" === a || "pointerout" === a;
        if (h2 && c !== wb && (n2 = c.relatedTarget || c.fromElement) && (Wc(n2) || n2[uf])) break a;
        if (k3 || h2) {
          h2 = e2.window === e2 ? e2 : (h2 = e2.ownerDocument) ? h2.defaultView || h2.parentWindow : window;
          if (k3) {
            if (n2 = c.relatedTarget || c.toElement, k3 = d2, n2 = n2 ? Wc(n2) : null, null !== n2 && (J2 = Vb(n2), n2 !== J2 || 5 !== n2.tag && 6 !== n2.tag)) n2 = null;
          } else k3 = null, n2 = d2;
          if (k3 !== n2) {
            t2 = Bd;
            F2 = "onMouseLeave";
            x2 = "onMouseEnter";
            w2 = "mouse";
            if ("pointerout" === a || "pointerover" === a) t2 = Td, F2 = "onPointerLeave", x2 = "onPointerEnter", w2 = "pointer";
            J2 = null == k3 ? h2 : ue(k3);
            u2 = null == n2 ? h2 : ue(n2);
            h2 = new t2(F2, w2 + "leave", k3, c, e2);
            h2.target = J2;
            h2.relatedTarget = u2;
            F2 = null;
            Wc(e2) === d2 && (t2 = new t2(x2, w2 + "enter", n2, c, e2), t2.target = u2, t2.relatedTarget = J2, F2 = t2);
            J2 = F2;
            if (k3 && n2) b: {
              t2 = k3;
              x2 = n2;
              w2 = 0;
              for (u2 = t2; u2; u2 = vf(u2)) w2++;
              u2 = 0;
              for (F2 = x2; F2; F2 = vf(F2)) u2++;
              for (; 0 < w2 - u2; ) t2 = vf(t2), w2--;
              for (; 0 < u2 - w2; ) x2 = vf(x2), u2--;
              for (; w2--; ) {
                if (t2 === x2 || null !== x2 && t2 === x2.alternate) break b;
                t2 = vf(t2);
                x2 = vf(x2);
              }
              t2 = null;
            }
            else t2 = null;
            null !== k3 && wf(g2, h2, k3, t2, false);
            null !== n2 && null !== J2 && wf(g2, J2, n2, t2, true);
          }
        }
      }
      a: {
        h2 = d2 ? ue(d2) : window;
        k3 = h2.nodeName && h2.nodeName.toLowerCase();
        if ("select" === k3 || "input" === k3 && "file" === h2.type) var na = ve;
        else if (me(h2)) if (we) na = Fe;
        else {
          na = De;
          var xa = Ce;
        }
        else (k3 = h2.nodeName) && "input" === k3.toLowerCase() && ("checkbox" === h2.type || "radio" === h2.type) && (na = Ee);
        if (na && (na = na(a, d2))) {
          ne(g2, na, c, e2);
          break a;
        }
        xa && xa(a, h2, d2);
        "focusout" === a && (xa = h2._wrapperState) && xa.controlled && "number" === h2.type && cb(h2, "number", h2.value);
      }
      xa = d2 ? ue(d2) : window;
      switch (a) {
        case "focusin":
          if (me(xa) || "true" === xa.contentEditable) Qe = xa, Re = d2, Se = null;
          break;
        case "focusout":
          Se = Re = Qe = null;
          break;
        case "mousedown":
          Te = true;
          break;
        case "contextmenu":
        case "mouseup":
        case "dragend":
          Te = false;
          Ue(g2, c, e2);
          break;
        case "selectionchange":
          if (Pe) break;
        case "keydown":
        case "keyup":
          Ue(g2, c, e2);
      }
      var $a;
      if (ae) b: {
        switch (a) {
          case "compositionstart":
            var ba = "onCompositionStart";
            break b;
          case "compositionend":
            ba = "onCompositionEnd";
            break b;
          case "compositionupdate":
            ba = "onCompositionUpdate";
            break b;
        }
        ba = void 0;
      }
      else ie ? ge(a, c) && (ba = "onCompositionEnd") : "keydown" === a && 229 === c.keyCode && (ba = "onCompositionStart");
      ba && (de && "ko" !== c.locale && (ie || "onCompositionStart" !== ba ? "onCompositionEnd" === ba && ie && ($a = nd()) : (kd = e2, ld = "value" in kd ? kd.value : kd.textContent, ie = true)), xa = oe(d2, ba), 0 < xa.length && (ba = new Ld(ba, a, null, c, e2), g2.push({ event: ba, listeners: xa }), $a ? ba.data = $a : ($a = he(c), null !== $a && (ba.data = $a))));
      if ($a = ce ? je(a, c) : ke(a, c)) d2 = oe(d2, "onBeforeInput"), 0 < d2.length && (e2 = new Ld("onBeforeInput", "beforeinput", null, c, e2), g2.push({ event: e2, listeners: d2 }), e2.data = $a);
    }
    se(g2, b);
  });
}
function tf(a, b, c) {
  return { instance: a, listener: b, currentTarget: c };
}
function oe(a, b) {
  for (var c = b + "Capture", d = []; null !== a; ) {
    var e = a, f2 = e.stateNode;
    5 === e.tag && null !== f2 && (e = f2, f2 = Kb(a, c), null != f2 && d.unshift(tf(a, f2, e)), f2 = Kb(a, b), null != f2 && d.push(tf(a, f2, e)));
    a = a.return;
  }
  return d;
}
function vf(a) {
  if (null === a) return null;
  do
    a = a.return;
  while (a && 5 !== a.tag);
  return a ? a : null;
}
function wf(a, b, c, d, e) {
  for (var f2 = b._reactName, g = []; null !== c && c !== d; ) {
    var h = c, k2 = h.alternate, l2 = h.stateNode;
    if (null !== k2 && k2 === d) break;
    5 === h.tag && null !== l2 && (h = l2, e ? (k2 = Kb(c, f2), null != k2 && g.unshift(tf(c, k2, h))) : e || (k2 = Kb(c, f2), null != k2 && g.push(tf(c, k2, h))));
    c = c.return;
  }
  0 !== g.length && a.push({ event: b, listeners: g });
}
var xf = /\r\n?/g, yf = /\u0000|\uFFFD/g;
function zf(a) {
  return ("string" === typeof a ? a : "" + a).replace(xf, "\n").replace(yf, "");
}
function Af(a, b, c) {
  b = zf(b);
  if (zf(a) !== b && c) throw Error(p(425));
}
function Bf() {
}
var Cf = null, Df = null;
function Ef(a, b) {
  return "textarea" === a || "noscript" === a || "string" === typeof b.children || "number" === typeof b.children || "object" === typeof b.dangerouslySetInnerHTML && null !== b.dangerouslySetInnerHTML && null != b.dangerouslySetInnerHTML.__html;
}
var Ff = "function" === typeof setTimeout ? setTimeout : void 0, Gf = "function" === typeof clearTimeout ? clearTimeout : void 0, Hf = "function" === typeof Promise ? Promise : void 0, Jf = "function" === typeof queueMicrotask ? queueMicrotask : "undefined" !== typeof Hf ? function(a) {
  return Hf.resolve(null).then(a).catch(If);
} : Ff;
function If(a) {
  setTimeout(function() {
    throw a;
  });
}
function Kf(a, b) {
  var c = b, d = 0;
  do {
    var e = c.nextSibling;
    a.removeChild(c);
    if (e && 8 === e.nodeType) if (c = e.data, "/$" === c) {
      if (0 === d) {
        a.removeChild(e);
        bd(b);
        return;
      }
      d--;
    } else "$" !== c && "$?" !== c && "$!" !== c || d++;
    c = e;
  } while (c);
  bd(b);
}
function Lf(a) {
  for (; null != a; a = a.nextSibling) {
    var b = a.nodeType;
    if (1 === b || 3 === b) break;
    if (8 === b) {
      b = a.data;
      if ("$" === b || "$!" === b || "$?" === b) break;
      if ("/$" === b) return null;
    }
  }
  return a;
}
function Mf(a) {
  a = a.previousSibling;
  for (var b = 0; a; ) {
    if (8 === a.nodeType) {
      var c = a.data;
      if ("$" === c || "$!" === c || "$?" === c) {
        if (0 === b) return a;
        b--;
      } else "/$" === c && b++;
    }
    a = a.previousSibling;
  }
  return null;
}
var Nf = Math.random().toString(36).slice(2), Of = "__reactFiber$" + Nf, Pf = "__reactProps$" + Nf, uf = "__reactContainer$" + Nf, of = "__reactEvents$" + Nf, Qf = "__reactListeners$" + Nf, Rf = "__reactHandles$" + Nf;
function Wc(a) {
  var b = a[Of];
  if (b) return b;
  for (var c = a.parentNode; c; ) {
    if (b = c[uf] || c[Of]) {
      c = b.alternate;
      if (null !== b.child || null !== c && null !== c.child) for (a = Mf(a); null !== a; ) {
        if (c = a[Of]) return c;
        a = Mf(a);
      }
      return b;
    }
    a = c;
    c = a.parentNode;
  }
  return null;
}
function Cb(a) {
  a = a[Of] || a[uf];
  return !a || 5 !== a.tag && 6 !== a.tag && 13 !== a.tag && 3 !== a.tag ? null : a;
}
function ue(a) {
  if (5 === a.tag || 6 === a.tag) return a.stateNode;
  throw Error(p(33));
}
function Db(a) {
  return a[Pf] || null;
}
var Sf = [], Tf = -1;
function Uf(a) {
  return { current: a };
}
function E(a) {
  0 > Tf || (a.current = Sf[Tf], Sf[Tf] = null, Tf--);
}
function G(a, b) {
  Tf++;
  Sf[Tf] = a.current;
  a.current = b;
}
var Vf = {}, H = Uf(Vf), Wf = Uf(false), Xf = Vf;
function Yf(a, b) {
  var c = a.type.contextTypes;
  if (!c) return Vf;
  var d = a.stateNode;
  if (d && d.__reactInternalMemoizedUnmaskedChildContext === b) return d.__reactInternalMemoizedMaskedChildContext;
  var e = {}, f2;
  for (f2 in c) e[f2] = b[f2];
  d && (a = a.stateNode, a.__reactInternalMemoizedUnmaskedChildContext = b, a.__reactInternalMemoizedMaskedChildContext = e);
  return e;
}
function Zf(a) {
  a = a.childContextTypes;
  return null !== a && void 0 !== a;
}
function $f() {
  E(Wf);
  E(H);
}
function ag(a, b, c) {
  if (H.current !== Vf) throw Error(p(168));
  G(H, b);
  G(Wf, c);
}
function bg(a, b, c) {
  var d = a.stateNode;
  b = b.childContextTypes;
  if ("function" !== typeof d.getChildContext) return c;
  d = d.getChildContext();
  for (var e in d) if (!(e in b)) throw Error(p(108, Ra(a) || "Unknown", e));
  return A({}, c, d);
}
function cg(a) {
  a = (a = a.stateNode) && a.__reactInternalMemoizedMergedChildContext || Vf;
  Xf = H.current;
  G(H, a);
  G(Wf, Wf.current);
  return true;
}
function dg(a, b, c) {
  var d = a.stateNode;
  if (!d) throw Error(p(169));
  c ? (a = bg(a, b, Xf), d.__reactInternalMemoizedMergedChildContext = a, E(Wf), E(H), G(H, a)) : E(Wf);
  G(Wf, c);
}
var eg = null, fg = false, gg = false;
function hg(a) {
  null === eg ? eg = [a] : eg.push(a);
}
function ig(a) {
  fg = true;
  hg(a);
}
function jg() {
  if (!gg && null !== eg) {
    gg = true;
    var a = 0, b = C;
    try {
      var c = eg;
      for (C = 1; a < c.length; a++) {
        var d = c[a];
        do
          d = d(true);
        while (null !== d);
      }
      eg = null;
      fg = false;
    } catch (e) {
      throw null !== eg && (eg = eg.slice(a + 1)), ac(fc, jg), e;
    } finally {
      C = b, gg = false;
    }
  }
  return null;
}
var kg = [], lg = 0, mg = null, ng = 0, og = [], pg = 0, qg = null, rg = 1, sg = "";
function tg(a, b) {
  kg[lg++] = ng;
  kg[lg++] = mg;
  mg = a;
  ng = b;
}
function ug(a, b, c) {
  og[pg++] = rg;
  og[pg++] = sg;
  og[pg++] = qg;
  qg = a;
  var d = rg;
  a = sg;
  var e = 32 - oc(d) - 1;
  d &= ~(1 << e);
  c += 1;
  var f2 = 32 - oc(b) + e;
  if (30 < f2) {
    var g = e - e % 5;
    f2 = (d & (1 << g) - 1).toString(32);
    d >>= g;
    e -= g;
    rg = 1 << 32 - oc(b) + e | c << e | d;
    sg = f2 + a;
  } else rg = 1 << f2 | c << e | d, sg = a;
}
function vg(a) {
  null !== a.return && (tg(a, 1), ug(a, 1, 0));
}
function wg(a) {
  for (; a === mg; ) mg = kg[--lg], kg[lg] = null, ng = kg[--lg], kg[lg] = null;
  for (; a === qg; ) qg = og[--pg], og[pg] = null, sg = og[--pg], og[pg] = null, rg = og[--pg], og[pg] = null;
}
var xg = null, yg = null, I = false, zg = null;
function Ag(a, b) {
  var c = Bg(5, null, null, 0);
  c.elementType = "DELETED";
  c.stateNode = b;
  c.return = a;
  b = a.deletions;
  null === b ? (a.deletions = [c], a.flags |= 16) : b.push(c);
}
function Cg(a, b) {
  switch (a.tag) {
    case 5:
      var c = a.type;
      b = 1 !== b.nodeType || c.toLowerCase() !== b.nodeName.toLowerCase() ? null : b;
      return null !== b ? (a.stateNode = b, xg = a, yg = Lf(b.firstChild), true) : false;
    case 6:
      return b = "" === a.pendingProps || 3 !== b.nodeType ? null : b, null !== b ? (a.stateNode = b, xg = a, yg = null, true) : false;
    case 13:
      return b = 8 !== b.nodeType ? null : b, null !== b ? (c = null !== qg ? { id: rg, overflow: sg } : null, a.memoizedState = { dehydrated: b, treeContext: c, retryLane: 1073741824 }, c = Bg(18, null, null, 0), c.stateNode = b, c.return = a, a.child = c, xg = a, yg = null, true) : false;
    default:
      return false;
  }
}
function Dg(a) {
  return 0 !== (a.mode & 1) && 0 === (a.flags & 128);
}
function Eg(a) {
  if (I) {
    var b = yg;
    if (b) {
      var c = b;
      if (!Cg(a, b)) {
        if (Dg(a)) throw Error(p(418));
        b = Lf(c.nextSibling);
        var d = xg;
        b && Cg(a, b) ? Ag(d, c) : (a.flags = a.flags & -4097 | 2, I = false, xg = a);
      }
    } else {
      if (Dg(a)) throw Error(p(418));
      a.flags = a.flags & -4097 | 2;
      I = false;
      xg = a;
    }
  }
}
function Fg(a) {
  for (a = a.return; null !== a && 5 !== a.tag && 3 !== a.tag && 13 !== a.tag; ) a = a.return;
  xg = a;
}
function Gg(a) {
  if (a !== xg) return false;
  if (!I) return Fg(a), I = true, false;
  var b;
  (b = 3 !== a.tag) && !(b = 5 !== a.tag) && (b = a.type, b = "head" !== b && "body" !== b && !Ef(a.type, a.memoizedProps));
  if (b && (b = yg)) {
    if (Dg(a)) throw Hg(), Error(p(418));
    for (; b; ) Ag(a, b), b = Lf(b.nextSibling);
  }
  Fg(a);
  if (13 === a.tag) {
    a = a.memoizedState;
    a = null !== a ? a.dehydrated : null;
    if (!a) throw Error(p(317));
    a: {
      a = a.nextSibling;
      for (b = 0; a; ) {
        if (8 === a.nodeType) {
          var c = a.data;
          if ("/$" === c) {
            if (0 === b) {
              yg = Lf(a.nextSibling);
              break a;
            }
            b--;
          } else "$" !== c && "$!" !== c && "$?" !== c || b++;
        }
        a = a.nextSibling;
      }
      yg = null;
    }
  } else yg = xg ? Lf(a.stateNode.nextSibling) : null;
  return true;
}
function Hg() {
  for (var a = yg; a; ) a = Lf(a.nextSibling);
}
function Ig() {
  yg = xg = null;
  I = false;
}
function Jg(a) {
  null === zg ? zg = [a] : zg.push(a);
}
var Kg = ua.ReactCurrentBatchConfig;
function Lg(a, b, c) {
  a = c.ref;
  if (null !== a && "function" !== typeof a && "object" !== typeof a) {
    if (c._owner) {
      c = c._owner;
      if (c) {
        if (1 !== c.tag) throw Error(p(309));
        var d = c.stateNode;
      }
      if (!d) throw Error(p(147, a));
      var e = d, f2 = "" + a;
      if (null !== b && null !== b.ref && "function" === typeof b.ref && b.ref._stringRef === f2) return b.ref;
      b = function(a2) {
        var b2 = e.refs;
        null === a2 ? delete b2[f2] : b2[f2] = a2;
      };
      b._stringRef = f2;
      return b;
    }
    if ("string" !== typeof a) throw Error(p(284));
    if (!c._owner) throw Error(p(290, a));
  }
  return a;
}
function Mg(a, b) {
  a = Object.prototype.toString.call(b);
  throw Error(p(31, "[object Object]" === a ? "object with keys {" + Object.keys(b).join(", ") + "}" : a));
}
function Ng(a) {
  var b = a._init;
  return b(a._payload);
}
function Og(a) {
  function b(b2, c2) {
    if (a) {
      var d2 = b2.deletions;
      null === d2 ? (b2.deletions = [c2], b2.flags |= 16) : d2.push(c2);
    }
  }
  function c(c2, d2) {
    if (!a) return null;
    for (; null !== d2; ) b(c2, d2), d2 = d2.sibling;
    return null;
  }
  function d(a2, b2) {
    for (a2 = /* @__PURE__ */ new Map(); null !== b2; ) null !== b2.key ? a2.set(b2.key, b2) : a2.set(b2.index, b2), b2 = b2.sibling;
    return a2;
  }
  function e(a2, b2) {
    a2 = Pg(a2, b2);
    a2.index = 0;
    a2.sibling = null;
    return a2;
  }
  function f2(b2, c2, d2) {
    b2.index = d2;
    if (!a) return b2.flags |= 1048576, c2;
    d2 = b2.alternate;
    if (null !== d2) return d2 = d2.index, d2 < c2 ? (b2.flags |= 2, c2) : d2;
    b2.flags |= 2;
    return c2;
  }
  function g(b2) {
    a && null === b2.alternate && (b2.flags |= 2);
    return b2;
  }
  function h(a2, b2, c2, d2) {
    if (null === b2 || 6 !== b2.tag) return b2 = Qg(c2, a2.mode, d2), b2.return = a2, b2;
    b2 = e(b2, c2);
    b2.return = a2;
    return b2;
  }
  function k2(a2, b2, c2, d2) {
    var f3 = c2.type;
    if (f3 === ya) return m2(a2, b2, c2.props.children, d2, c2.key);
    if (null !== b2 && (b2.elementType === f3 || "object" === typeof f3 && null !== f3 && f3.$$typeof === Ha && Ng(f3) === b2.type)) return d2 = e(b2, c2.props), d2.ref = Lg(a2, b2, c2), d2.return = a2, d2;
    d2 = Rg(c2.type, c2.key, c2.props, null, a2.mode, d2);
    d2.ref = Lg(a2, b2, c2);
    d2.return = a2;
    return d2;
  }
  function l2(a2, b2, c2, d2) {
    if (null === b2 || 4 !== b2.tag || b2.stateNode.containerInfo !== c2.containerInfo || b2.stateNode.implementation !== c2.implementation) return b2 = Sg(c2, a2.mode, d2), b2.return = a2, b2;
    b2 = e(b2, c2.children || []);
    b2.return = a2;
    return b2;
  }
  function m2(a2, b2, c2, d2, f3) {
    if (null === b2 || 7 !== b2.tag) return b2 = Tg(c2, a2.mode, d2, f3), b2.return = a2, b2;
    b2 = e(b2, c2);
    b2.return = a2;
    return b2;
  }
  function q2(a2, b2, c2) {
    if ("string" === typeof b2 && "" !== b2 || "number" === typeof b2) return b2 = Qg("" + b2, a2.mode, c2), b2.return = a2, b2;
    if ("object" === typeof b2 && null !== b2) {
      switch (b2.$$typeof) {
        case va:
          return c2 = Rg(b2.type, b2.key, b2.props, null, a2.mode, c2), c2.ref = Lg(a2, null, b2), c2.return = a2, c2;
        case wa:
          return b2 = Sg(b2, a2.mode, c2), b2.return = a2, b2;
        case Ha:
          var d2 = b2._init;
          return q2(a2, d2(b2._payload), c2);
      }
      if (eb(b2) || Ka(b2)) return b2 = Tg(b2, a2.mode, c2, null), b2.return = a2, b2;
      Mg(a2, b2);
    }
    return null;
  }
  function r2(a2, b2, c2, d2) {
    var e2 = null !== b2 ? b2.key : null;
    if ("string" === typeof c2 && "" !== c2 || "number" === typeof c2) return null !== e2 ? null : h(a2, b2, "" + c2, d2);
    if ("object" === typeof c2 && null !== c2) {
      switch (c2.$$typeof) {
        case va:
          return c2.key === e2 ? k2(a2, b2, c2, d2) : null;
        case wa:
          return c2.key === e2 ? l2(a2, b2, c2, d2) : null;
        case Ha:
          return e2 = c2._init, r2(
            a2,
            b2,
            e2(c2._payload),
            d2
          );
      }
      if (eb(c2) || Ka(c2)) return null !== e2 ? null : m2(a2, b2, c2, d2, null);
      Mg(a2, c2);
    }
    return null;
  }
  function y2(a2, b2, c2, d2, e2) {
    if ("string" === typeof d2 && "" !== d2 || "number" === typeof d2) return a2 = a2.get(c2) || null, h(b2, a2, "" + d2, e2);
    if ("object" === typeof d2 && null !== d2) {
      switch (d2.$$typeof) {
        case va:
          return a2 = a2.get(null === d2.key ? c2 : d2.key) || null, k2(b2, a2, d2, e2);
        case wa:
          return a2 = a2.get(null === d2.key ? c2 : d2.key) || null, l2(b2, a2, d2, e2);
        case Ha:
          var f3 = d2._init;
          return y2(a2, b2, c2, f3(d2._payload), e2);
      }
      if (eb(d2) || Ka(d2)) return a2 = a2.get(c2) || null, m2(b2, a2, d2, e2, null);
      Mg(b2, d2);
    }
    return null;
  }
  function n2(e2, g2, h2, k3) {
    for (var l3 = null, m3 = null, u2 = g2, w2 = g2 = 0, x2 = null; null !== u2 && w2 < h2.length; w2++) {
      u2.index > w2 ? (x2 = u2, u2 = null) : x2 = u2.sibling;
      var n3 = r2(e2, u2, h2[w2], k3);
      if (null === n3) {
        null === u2 && (u2 = x2);
        break;
      }
      a && u2 && null === n3.alternate && b(e2, u2);
      g2 = f2(n3, g2, w2);
      null === m3 ? l3 = n3 : m3.sibling = n3;
      m3 = n3;
      u2 = x2;
    }
    if (w2 === h2.length) return c(e2, u2), I && tg(e2, w2), l3;
    if (null === u2) {
      for (; w2 < h2.length; w2++) u2 = q2(e2, h2[w2], k3), null !== u2 && (g2 = f2(u2, g2, w2), null === m3 ? l3 = u2 : m3.sibling = u2, m3 = u2);
      I && tg(e2, w2);
      return l3;
    }
    for (u2 = d(e2, u2); w2 < h2.length; w2++) x2 = y2(u2, e2, w2, h2[w2], k3), null !== x2 && (a && null !== x2.alternate && u2.delete(null === x2.key ? w2 : x2.key), g2 = f2(x2, g2, w2), null === m3 ? l3 = x2 : m3.sibling = x2, m3 = x2);
    a && u2.forEach(function(a2) {
      return b(e2, a2);
    });
    I && tg(e2, w2);
    return l3;
  }
  function t2(e2, g2, h2, k3) {
    var l3 = Ka(h2);
    if ("function" !== typeof l3) throw Error(p(150));
    h2 = l3.call(h2);
    if (null == h2) throw Error(p(151));
    for (var u2 = l3 = null, m3 = g2, w2 = g2 = 0, x2 = null, n3 = h2.next(); null !== m3 && !n3.done; w2++, n3 = h2.next()) {
      m3.index > w2 ? (x2 = m3, m3 = null) : x2 = m3.sibling;
      var t3 = r2(e2, m3, n3.value, k3);
      if (null === t3) {
        null === m3 && (m3 = x2);
        break;
      }
      a && m3 && null === t3.alternate && b(e2, m3);
      g2 = f2(t3, g2, w2);
      null === u2 ? l3 = t3 : u2.sibling = t3;
      u2 = t3;
      m3 = x2;
    }
    if (n3.done) return c(
      e2,
      m3
    ), I && tg(e2, w2), l3;
    if (null === m3) {
      for (; !n3.done; w2++, n3 = h2.next()) n3 = q2(e2, n3.value, k3), null !== n3 && (g2 = f2(n3, g2, w2), null === u2 ? l3 = n3 : u2.sibling = n3, u2 = n3);
      I && tg(e2, w2);
      return l3;
    }
    for (m3 = d(e2, m3); !n3.done; w2++, n3 = h2.next()) n3 = y2(m3, e2, w2, n3.value, k3), null !== n3 && (a && null !== n3.alternate && m3.delete(null === n3.key ? w2 : n3.key), g2 = f2(n3, g2, w2), null === u2 ? l3 = n3 : u2.sibling = n3, u2 = n3);
    a && m3.forEach(function(a2) {
      return b(e2, a2);
    });
    I && tg(e2, w2);
    return l3;
  }
  function J2(a2, d2, f3, h2) {
    "object" === typeof f3 && null !== f3 && f3.type === ya && null === f3.key && (f3 = f3.props.children);
    if ("object" === typeof f3 && null !== f3) {
      switch (f3.$$typeof) {
        case va:
          a: {
            for (var k3 = f3.key, l3 = d2; null !== l3; ) {
              if (l3.key === k3) {
                k3 = f3.type;
                if (k3 === ya) {
                  if (7 === l3.tag) {
                    c(a2, l3.sibling);
                    d2 = e(l3, f3.props.children);
                    d2.return = a2;
                    a2 = d2;
                    break a;
                  }
                } else if (l3.elementType === k3 || "object" === typeof k3 && null !== k3 && k3.$$typeof === Ha && Ng(k3) === l3.type) {
                  c(a2, l3.sibling);
                  d2 = e(l3, f3.props);
                  d2.ref = Lg(a2, l3, f3);
                  d2.return = a2;
                  a2 = d2;
                  break a;
                }
                c(a2, l3);
                break;
              } else b(a2, l3);
              l3 = l3.sibling;
            }
            f3.type === ya ? (d2 = Tg(f3.props.children, a2.mode, h2, f3.key), d2.return = a2, a2 = d2) : (h2 = Rg(f3.type, f3.key, f3.props, null, a2.mode, h2), h2.ref = Lg(a2, d2, f3), h2.return = a2, a2 = h2);
          }
          return g(a2);
        case wa:
          a: {
            for (l3 = f3.key; null !== d2; ) {
              if (d2.key === l3) if (4 === d2.tag && d2.stateNode.containerInfo === f3.containerInfo && d2.stateNode.implementation === f3.implementation) {
                c(a2, d2.sibling);
                d2 = e(d2, f3.children || []);
                d2.return = a2;
                a2 = d2;
                break a;
              } else {
                c(a2, d2);
                break;
              }
              else b(a2, d2);
              d2 = d2.sibling;
            }
            d2 = Sg(f3, a2.mode, h2);
            d2.return = a2;
            a2 = d2;
          }
          return g(a2);
        case Ha:
          return l3 = f3._init, J2(a2, d2, l3(f3._payload), h2);
      }
      if (eb(f3)) return n2(a2, d2, f3, h2);
      if (Ka(f3)) return t2(a2, d2, f3, h2);
      Mg(a2, f3);
    }
    return "string" === typeof f3 && "" !== f3 || "number" === typeof f3 ? (f3 = "" + f3, null !== d2 && 6 === d2.tag ? (c(a2, d2.sibling), d2 = e(d2, f3), d2.return = a2, a2 = d2) : (c(a2, d2), d2 = Qg(f3, a2.mode, h2), d2.return = a2, a2 = d2), g(a2)) : c(a2, d2);
  }
  return J2;
}
var Ug = Og(true), Vg = Og(false), Wg = Uf(null), Xg = null, Yg = null, Zg = null;
function $g() {
  Zg = Yg = Xg = null;
}
function ah(a) {
  var b = Wg.current;
  E(Wg);
  a._currentValue = b;
}
function bh(a, b, c) {
  for (; null !== a; ) {
    var d = a.alternate;
    (a.childLanes & b) !== b ? (a.childLanes |= b, null !== d && (d.childLanes |= b)) : null !== d && (d.childLanes & b) !== b && (d.childLanes |= b);
    if (a === c) break;
    a = a.return;
  }
}
function ch(a, b) {
  Xg = a;
  Zg = Yg = null;
  a = a.dependencies;
  null !== a && null !== a.firstContext && (0 !== (a.lanes & b) && (dh = true), a.firstContext = null);
}
function eh(a) {
  var b = a._currentValue;
  if (Zg !== a) if (a = { context: a, memoizedValue: b, next: null }, null === Yg) {
    if (null === Xg) throw Error(p(308));
    Yg = a;
    Xg.dependencies = { lanes: 0, firstContext: a };
  } else Yg = Yg.next = a;
  return b;
}
var fh = null;
function gh(a) {
  null === fh ? fh = [a] : fh.push(a);
}
function hh(a, b, c, d) {
  var e = b.interleaved;
  null === e ? (c.next = c, gh(b)) : (c.next = e.next, e.next = c);
  b.interleaved = c;
  return ih(a, d);
}
function ih(a, b) {
  a.lanes |= b;
  var c = a.alternate;
  null !== c && (c.lanes |= b);
  c = a;
  for (a = a.return; null !== a; ) a.childLanes |= b, c = a.alternate, null !== c && (c.childLanes |= b), c = a, a = a.return;
  return 3 === c.tag ? c.stateNode : null;
}
var jh = false;
function kh(a) {
  a.updateQueue = { baseState: a.memoizedState, firstBaseUpdate: null, lastBaseUpdate: null, shared: { pending: null, interleaved: null, lanes: 0 }, effects: null };
}
function lh(a, b) {
  a = a.updateQueue;
  b.updateQueue === a && (b.updateQueue = { baseState: a.baseState, firstBaseUpdate: a.firstBaseUpdate, lastBaseUpdate: a.lastBaseUpdate, shared: a.shared, effects: a.effects });
}
function mh(a, b) {
  return { eventTime: a, lane: b, tag: 0, payload: null, callback: null, next: null };
}
function nh(a, b, c) {
  var d = a.updateQueue;
  if (null === d) return null;
  d = d.shared;
  if (0 !== (K & 2)) {
    var e = d.pending;
    null === e ? b.next = b : (b.next = e.next, e.next = b);
    d.pending = b;
    return ih(a, c);
  }
  e = d.interleaved;
  null === e ? (b.next = b, gh(d)) : (b.next = e.next, e.next = b);
  d.interleaved = b;
  return ih(a, c);
}
function oh(a, b, c) {
  b = b.updateQueue;
  if (null !== b && (b = b.shared, 0 !== (c & 4194240))) {
    var d = b.lanes;
    d &= a.pendingLanes;
    c |= d;
    b.lanes = c;
    Cc(a, c);
  }
}
function ph(a, b) {
  var c = a.updateQueue, d = a.alternate;
  if (null !== d && (d = d.updateQueue, c === d)) {
    var e = null, f2 = null;
    c = c.firstBaseUpdate;
    if (null !== c) {
      do {
        var g = { eventTime: c.eventTime, lane: c.lane, tag: c.tag, payload: c.payload, callback: c.callback, next: null };
        null === f2 ? e = f2 = g : f2 = f2.next = g;
        c = c.next;
      } while (null !== c);
      null === f2 ? e = f2 = b : f2 = f2.next = b;
    } else e = f2 = b;
    c = { baseState: d.baseState, firstBaseUpdate: e, lastBaseUpdate: f2, shared: d.shared, effects: d.effects };
    a.updateQueue = c;
    return;
  }
  a = c.lastBaseUpdate;
  null === a ? c.firstBaseUpdate = b : a.next = b;
  c.lastBaseUpdate = b;
}
function qh(a, b, c, d) {
  var e = a.updateQueue;
  jh = false;
  var f2 = e.firstBaseUpdate, g = e.lastBaseUpdate, h = e.shared.pending;
  if (null !== h) {
    e.shared.pending = null;
    var k2 = h, l2 = k2.next;
    k2.next = null;
    null === g ? f2 = l2 : g.next = l2;
    g = k2;
    var m2 = a.alternate;
    null !== m2 && (m2 = m2.updateQueue, h = m2.lastBaseUpdate, h !== g && (null === h ? m2.firstBaseUpdate = l2 : h.next = l2, m2.lastBaseUpdate = k2));
  }
  if (null !== f2) {
    var q2 = e.baseState;
    g = 0;
    m2 = l2 = k2 = null;
    h = f2;
    do {
      var r2 = h.lane, y2 = h.eventTime;
      if ((d & r2) === r2) {
        null !== m2 && (m2 = m2.next = {
          eventTime: y2,
          lane: 0,
          tag: h.tag,
          payload: h.payload,
          callback: h.callback,
          next: null
        });
        a: {
          var n2 = a, t2 = h;
          r2 = b;
          y2 = c;
          switch (t2.tag) {
            case 1:
              n2 = t2.payload;
              if ("function" === typeof n2) {
                q2 = n2.call(y2, q2, r2);
                break a;
              }
              q2 = n2;
              break a;
            case 3:
              n2.flags = n2.flags & -65537 | 128;
            case 0:
              n2 = t2.payload;
              r2 = "function" === typeof n2 ? n2.call(y2, q2, r2) : n2;
              if (null === r2 || void 0 === r2) break a;
              q2 = A({}, q2, r2);
              break a;
            case 2:
              jh = true;
          }
        }
        null !== h.callback && 0 !== h.lane && (a.flags |= 64, r2 = e.effects, null === r2 ? e.effects = [h] : r2.push(h));
      } else y2 = { eventTime: y2, lane: r2, tag: h.tag, payload: h.payload, callback: h.callback, next: null }, null === m2 ? (l2 = m2 = y2, k2 = q2) : m2 = m2.next = y2, g |= r2;
      h = h.next;
      if (null === h) if (h = e.shared.pending, null === h) break;
      else r2 = h, h = r2.next, r2.next = null, e.lastBaseUpdate = r2, e.shared.pending = null;
    } while (1);
    null === m2 && (k2 = q2);
    e.baseState = k2;
    e.firstBaseUpdate = l2;
    e.lastBaseUpdate = m2;
    b = e.shared.interleaved;
    if (null !== b) {
      e = b;
      do
        g |= e.lane, e = e.next;
      while (e !== b);
    } else null === f2 && (e.shared.lanes = 0);
    rh |= g;
    a.lanes = g;
    a.memoizedState = q2;
  }
}
function sh(a, b, c) {
  a = b.effects;
  b.effects = null;
  if (null !== a) for (b = 0; b < a.length; b++) {
    var d = a[b], e = d.callback;
    if (null !== e) {
      d.callback = null;
      d = c;
      if ("function" !== typeof e) throw Error(p(191, e));
      e.call(d);
    }
  }
}
var th = {}, uh = Uf(th), vh = Uf(th), wh = Uf(th);
function xh(a) {
  if (a === th) throw Error(p(174));
  return a;
}
function yh(a, b) {
  G(wh, b);
  G(vh, a);
  G(uh, th);
  a = b.nodeType;
  switch (a) {
    case 9:
    case 11:
      b = (b = b.documentElement) ? b.namespaceURI : lb(null, "");
      break;
    default:
      a = 8 === a ? b.parentNode : b, b = a.namespaceURI || null, a = a.tagName, b = lb(b, a);
  }
  E(uh);
  G(uh, b);
}
function zh() {
  E(uh);
  E(vh);
  E(wh);
}
function Ah(a) {
  xh(wh.current);
  var b = xh(uh.current);
  var c = lb(b, a.type);
  b !== c && (G(vh, a), G(uh, c));
}
function Bh(a) {
  vh.current === a && (E(uh), E(vh));
}
var L = Uf(0);
function Ch(a) {
  for (var b = a; null !== b; ) {
    if (13 === b.tag) {
      var c = b.memoizedState;
      if (null !== c && (c = c.dehydrated, null === c || "$?" === c.data || "$!" === c.data)) return b;
    } else if (19 === b.tag && void 0 !== b.memoizedProps.revealOrder) {
      if (0 !== (b.flags & 128)) return b;
    } else if (null !== b.child) {
      b.child.return = b;
      b = b.child;
      continue;
    }
    if (b === a) break;
    for (; null === b.sibling; ) {
      if (null === b.return || b.return === a) return null;
      b = b.return;
    }
    b.sibling.return = b.return;
    b = b.sibling;
  }
  return null;
}
var Dh = [];
function Eh() {
  for (var a = 0; a < Dh.length; a++) Dh[a]._workInProgressVersionPrimary = null;
  Dh.length = 0;
}
var Fh = ua.ReactCurrentDispatcher, Gh = ua.ReactCurrentBatchConfig, Hh = 0, M = null, N = null, O = null, Ih = false, Jh = false, Kh = 0, Lh = 0;
function P() {
  throw Error(p(321));
}
function Mh(a, b) {
  if (null === b) return false;
  for (var c = 0; c < b.length && c < a.length; c++) if (!He(a[c], b[c])) return false;
  return true;
}
function Nh(a, b, c, d, e, f2) {
  Hh = f2;
  M = b;
  b.memoizedState = null;
  b.updateQueue = null;
  b.lanes = 0;
  Fh.current = null === a || null === a.memoizedState ? Oh : Ph;
  a = c(d, e);
  if (Jh) {
    f2 = 0;
    do {
      Jh = false;
      Kh = 0;
      if (25 <= f2) throw Error(p(301));
      f2 += 1;
      O = N = null;
      b.updateQueue = null;
      Fh.current = Qh;
      a = c(d, e);
    } while (Jh);
  }
  Fh.current = Rh;
  b = null !== N && null !== N.next;
  Hh = 0;
  O = N = M = null;
  Ih = false;
  if (b) throw Error(p(300));
  return a;
}
function Sh() {
  var a = 0 !== Kh;
  Kh = 0;
  return a;
}
function Th() {
  var a = { memoizedState: null, baseState: null, baseQueue: null, queue: null, next: null };
  null === O ? M.memoizedState = O = a : O = O.next = a;
  return O;
}
function Uh() {
  if (null === N) {
    var a = M.alternate;
    a = null !== a ? a.memoizedState : null;
  } else a = N.next;
  var b = null === O ? M.memoizedState : O.next;
  if (null !== b) O = b, N = a;
  else {
    if (null === a) throw Error(p(310));
    N = a;
    a = { memoizedState: N.memoizedState, baseState: N.baseState, baseQueue: N.baseQueue, queue: N.queue, next: null };
    null === O ? M.memoizedState = O = a : O = O.next = a;
  }
  return O;
}
function Vh(a, b) {
  return "function" === typeof b ? b(a) : b;
}
function Wh(a) {
  var b = Uh(), c = b.queue;
  if (null === c) throw Error(p(311));
  c.lastRenderedReducer = a;
  var d = N, e = d.baseQueue, f2 = c.pending;
  if (null !== f2) {
    if (null !== e) {
      var g = e.next;
      e.next = f2.next;
      f2.next = g;
    }
    d.baseQueue = e = f2;
    c.pending = null;
  }
  if (null !== e) {
    f2 = e.next;
    d = d.baseState;
    var h = g = null, k2 = null, l2 = f2;
    do {
      var m2 = l2.lane;
      if ((Hh & m2) === m2) null !== k2 && (k2 = k2.next = { lane: 0, action: l2.action, hasEagerState: l2.hasEagerState, eagerState: l2.eagerState, next: null }), d = l2.hasEagerState ? l2.eagerState : a(d, l2.action);
      else {
        var q2 = {
          lane: m2,
          action: l2.action,
          hasEagerState: l2.hasEagerState,
          eagerState: l2.eagerState,
          next: null
        };
        null === k2 ? (h = k2 = q2, g = d) : k2 = k2.next = q2;
        M.lanes |= m2;
        rh |= m2;
      }
      l2 = l2.next;
    } while (null !== l2 && l2 !== f2);
    null === k2 ? g = d : k2.next = h;
    He(d, b.memoizedState) || (dh = true);
    b.memoizedState = d;
    b.baseState = g;
    b.baseQueue = k2;
    c.lastRenderedState = d;
  }
  a = c.interleaved;
  if (null !== a) {
    e = a;
    do
      f2 = e.lane, M.lanes |= f2, rh |= f2, e = e.next;
    while (e !== a);
  } else null === e && (c.lanes = 0);
  return [b.memoizedState, c.dispatch];
}
function Xh(a) {
  var b = Uh(), c = b.queue;
  if (null === c) throw Error(p(311));
  c.lastRenderedReducer = a;
  var d = c.dispatch, e = c.pending, f2 = b.memoizedState;
  if (null !== e) {
    c.pending = null;
    var g = e = e.next;
    do
      f2 = a(f2, g.action), g = g.next;
    while (g !== e);
    He(f2, b.memoizedState) || (dh = true);
    b.memoizedState = f2;
    null === b.baseQueue && (b.baseState = f2);
    c.lastRenderedState = f2;
  }
  return [f2, d];
}
function Yh() {
}
function Zh(a, b) {
  var c = M, d = Uh(), e = b(), f2 = !He(d.memoizedState, e);
  f2 && (d.memoizedState = e, dh = true);
  d = d.queue;
  $h(ai.bind(null, c, d, a), [a]);
  if (d.getSnapshot !== b || f2 || null !== O && O.memoizedState.tag & 1) {
    c.flags |= 2048;
    bi(9, ci.bind(null, c, d, e, b), void 0, null);
    if (null === Q) throw Error(p(349));
    0 !== (Hh & 30) || di(c, b, e);
  }
  return e;
}
function di(a, b, c) {
  a.flags |= 16384;
  a = { getSnapshot: b, value: c };
  b = M.updateQueue;
  null === b ? (b = { lastEffect: null, stores: null }, M.updateQueue = b, b.stores = [a]) : (c = b.stores, null === c ? b.stores = [a] : c.push(a));
}
function ci(a, b, c, d) {
  b.value = c;
  b.getSnapshot = d;
  ei(b) && fi(a);
}
function ai(a, b, c) {
  return c(function() {
    ei(b) && fi(a);
  });
}
function ei(a) {
  var b = a.getSnapshot;
  a = a.value;
  try {
    var c = b();
    return !He(a, c);
  } catch (d) {
    return true;
  }
}
function fi(a) {
  var b = ih(a, 1);
  null !== b && gi(b, a, 1, -1);
}
function hi(a) {
  var b = Th();
  "function" === typeof a && (a = a());
  b.memoizedState = b.baseState = a;
  a = { pending: null, interleaved: null, lanes: 0, dispatch: null, lastRenderedReducer: Vh, lastRenderedState: a };
  b.queue = a;
  a = a.dispatch = ii.bind(null, M, a);
  return [b.memoizedState, a];
}
function bi(a, b, c, d) {
  a = { tag: a, create: b, destroy: c, deps: d, next: null };
  b = M.updateQueue;
  null === b ? (b = { lastEffect: null, stores: null }, M.updateQueue = b, b.lastEffect = a.next = a) : (c = b.lastEffect, null === c ? b.lastEffect = a.next = a : (d = c.next, c.next = a, a.next = d, b.lastEffect = a));
  return a;
}
function ji() {
  return Uh().memoizedState;
}
function ki(a, b, c, d) {
  var e = Th();
  M.flags |= a;
  e.memoizedState = bi(1 | b, c, void 0, void 0 === d ? null : d);
}
function li(a, b, c, d) {
  var e = Uh();
  d = void 0 === d ? null : d;
  var f2 = void 0;
  if (null !== N) {
    var g = N.memoizedState;
    f2 = g.destroy;
    if (null !== d && Mh(d, g.deps)) {
      e.memoizedState = bi(b, c, f2, d);
      return;
    }
  }
  M.flags |= a;
  e.memoizedState = bi(1 | b, c, f2, d);
}
function mi(a, b) {
  return ki(8390656, 8, a, b);
}
function $h(a, b) {
  return li(2048, 8, a, b);
}
function ni(a, b) {
  return li(4, 2, a, b);
}
function oi(a, b) {
  return li(4, 4, a, b);
}
function pi(a, b) {
  if ("function" === typeof b) return a = a(), b(a), function() {
    b(null);
  };
  if (null !== b && void 0 !== b) return a = a(), b.current = a, function() {
    b.current = null;
  };
}
function qi(a, b, c) {
  c = null !== c && void 0 !== c ? c.concat([a]) : null;
  return li(4, 4, pi.bind(null, b, a), c);
}
function ri() {
}
function si(a, b) {
  var c = Uh();
  b = void 0 === b ? null : b;
  var d = c.memoizedState;
  if (null !== d && null !== b && Mh(b, d[1])) return d[0];
  c.memoizedState = [a, b];
  return a;
}
function ti(a, b) {
  var c = Uh();
  b = void 0 === b ? null : b;
  var d = c.memoizedState;
  if (null !== d && null !== b && Mh(b, d[1])) return d[0];
  a = a();
  c.memoizedState = [a, b];
  return a;
}
function ui(a, b, c) {
  if (0 === (Hh & 21)) return a.baseState && (a.baseState = false, dh = true), a.memoizedState = c;
  He(c, b) || (c = yc(), M.lanes |= c, rh |= c, a.baseState = true);
  return b;
}
function vi(a, b) {
  var c = C;
  C = 0 !== c && 4 > c ? c : 4;
  a(true);
  var d = Gh.transition;
  Gh.transition = {};
  try {
    a(false), b();
  } finally {
    C = c, Gh.transition = d;
  }
}
function wi() {
  return Uh().memoizedState;
}
function xi(a, b, c) {
  var d = yi(a);
  c = { lane: d, action: c, hasEagerState: false, eagerState: null, next: null };
  if (zi(a)) Ai(b, c);
  else if (c = hh(a, b, c, d), null !== c) {
    var e = R();
    gi(c, a, d, e);
    Bi(c, b, d);
  }
}
function ii(a, b, c) {
  var d = yi(a), e = { lane: d, action: c, hasEagerState: false, eagerState: null, next: null };
  if (zi(a)) Ai(b, e);
  else {
    var f2 = a.alternate;
    if (0 === a.lanes && (null === f2 || 0 === f2.lanes) && (f2 = b.lastRenderedReducer, null !== f2)) try {
      var g = b.lastRenderedState, h = f2(g, c);
      e.hasEagerState = true;
      e.eagerState = h;
      if (He(h, g)) {
        var k2 = b.interleaved;
        null === k2 ? (e.next = e, gh(b)) : (e.next = k2.next, k2.next = e);
        b.interleaved = e;
        return;
      }
    } catch (l2) {
    } finally {
    }
    c = hh(a, b, e, d);
    null !== c && (e = R(), gi(c, a, d, e), Bi(c, b, d));
  }
}
function zi(a) {
  var b = a.alternate;
  return a === M || null !== b && b === M;
}
function Ai(a, b) {
  Jh = Ih = true;
  var c = a.pending;
  null === c ? b.next = b : (b.next = c.next, c.next = b);
  a.pending = b;
}
function Bi(a, b, c) {
  if (0 !== (c & 4194240)) {
    var d = b.lanes;
    d &= a.pendingLanes;
    c |= d;
    b.lanes = c;
    Cc(a, c);
  }
}
var Rh = { readContext: eh, useCallback: P, useContext: P, useEffect: P, useImperativeHandle: P, useInsertionEffect: P, useLayoutEffect: P, useMemo: P, useReducer: P, useRef: P, useState: P, useDebugValue: P, useDeferredValue: P, useTransition: P, useMutableSource: P, useSyncExternalStore: P, useId: P, unstable_isNewReconciler: false }, Oh = { readContext: eh, useCallback: function(a, b) {
  Th().memoizedState = [a, void 0 === b ? null : b];
  return a;
}, useContext: eh, useEffect: mi, useImperativeHandle: function(a, b, c) {
  c = null !== c && void 0 !== c ? c.concat([a]) : null;
  return ki(
    4194308,
    4,
    pi.bind(null, b, a),
    c
  );
}, useLayoutEffect: function(a, b) {
  return ki(4194308, 4, a, b);
}, useInsertionEffect: function(a, b) {
  return ki(4, 2, a, b);
}, useMemo: function(a, b) {
  var c = Th();
  b = void 0 === b ? null : b;
  a = a();
  c.memoizedState = [a, b];
  return a;
}, useReducer: function(a, b, c) {
  var d = Th();
  b = void 0 !== c ? c(b) : b;
  d.memoizedState = d.baseState = b;
  a = { pending: null, interleaved: null, lanes: 0, dispatch: null, lastRenderedReducer: a, lastRenderedState: b };
  d.queue = a;
  a = a.dispatch = xi.bind(null, M, a);
  return [d.memoizedState, a];
}, useRef: function(a) {
  var b = Th();
  a = { current: a };
  return b.memoizedState = a;
}, useState: hi, useDebugValue: ri, useDeferredValue: function(a) {
  return Th().memoizedState = a;
}, useTransition: function() {
  var a = hi(false), b = a[0];
  a = vi.bind(null, a[1]);
  Th().memoizedState = a;
  return [b, a];
}, useMutableSource: function() {
}, useSyncExternalStore: function(a, b, c) {
  var d = M, e = Th();
  if (I) {
    if (void 0 === c) throw Error(p(407));
    c = c();
  } else {
    c = b();
    if (null === Q) throw Error(p(349));
    0 !== (Hh & 30) || di(d, b, c);
  }
  e.memoizedState = c;
  var f2 = { value: c, getSnapshot: b };
  e.queue = f2;
  mi(ai.bind(
    null,
    d,
    f2,
    a
  ), [a]);
  d.flags |= 2048;
  bi(9, ci.bind(null, d, f2, c, b), void 0, null);
  return c;
}, useId: function() {
  var a = Th(), b = Q.identifierPrefix;
  if (I) {
    var c = sg;
    var d = rg;
    c = (d & ~(1 << 32 - oc(d) - 1)).toString(32) + c;
    b = ":" + b + "R" + c;
    c = Kh++;
    0 < c && (b += "H" + c.toString(32));
    b += ":";
  } else c = Lh++, b = ":" + b + "r" + c.toString(32) + ":";
  return a.memoizedState = b;
}, unstable_isNewReconciler: false }, Ph = {
  readContext: eh,
  useCallback: si,
  useContext: eh,
  useEffect: $h,
  useImperativeHandle: qi,
  useInsertionEffect: ni,
  useLayoutEffect: oi,
  useMemo: ti,
  useReducer: Wh,
  useRef: ji,
  useState: function() {
    return Wh(Vh);
  },
  useDebugValue: ri,
  useDeferredValue: function(a) {
    var b = Uh();
    return ui(b, N.memoizedState, a);
  },
  useTransition: function() {
    var a = Wh(Vh)[0], b = Uh().memoizedState;
    return [a, b];
  },
  useMutableSource: Yh,
  useSyncExternalStore: Zh,
  useId: wi,
  unstable_isNewReconciler: false
}, Qh = { readContext: eh, useCallback: si, useContext: eh, useEffect: $h, useImperativeHandle: qi, useInsertionEffect: ni, useLayoutEffect: oi, useMemo: ti, useReducer: Xh, useRef: ji, useState: function() {
  return Xh(Vh);
}, useDebugValue: ri, useDeferredValue: function(a) {
  var b = Uh();
  return null === N ? b.memoizedState = a : ui(b, N.memoizedState, a);
}, useTransition: function() {
  var a = Xh(Vh)[0], b = Uh().memoizedState;
  return [a, b];
}, useMutableSource: Yh, useSyncExternalStore: Zh, useId: wi, unstable_isNewReconciler: false };
function Ci(a, b) {
  if (a && a.defaultProps) {
    b = A({}, b);
    a = a.defaultProps;
    for (var c in a) void 0 === b[c] && (b[c] = a[c]);
    return b;
  }
  return b;
}
function Di(a, b, c, d) {
  b = a.memoizedState;
  c = c(d, b);
  c = null === c || void 0 === c ? b : A({}, b, c);
  a.memoizedState = c;
  0 === a.lanes && (a.updateQueue.baseState = c);
}
var Ei = { isMounted: function(a) {
  return (a = a._reactInternals) ? Vb(a) === a : false;
}, enqueueSetState: function(a, b, c) {
  a = a._reactInternals;
  var d = R(), e = yi(a), f2 = mh(d, e);
  f2.payload = b;
  void 0 !== c && null !== c && (f2.callback = c);
  b = nh(a, f2, e);
  null !== b && (gi(b, a, e, d), oh(b, a, e));
}, enqueueReplaceState: function(a, b, c) {
  a = a._reactInternals;
  var d = R(), e = yi(a), f2 = mh(d, e);
  f2.tag = 1;
  f2.payload = b;
  void 0 !== c && null !== c && (f2.callback = c);
  b = nh(a, f2, e);
  null !== b && (gi(b, a, e, d), oh(b, a, e));
}, enqueueForceUpdate: function(a, b) {
  a = a._reactInternals;
  var c = R(), d = yi(a), e = mh(c, d);
  e.tag = 2;
  void 0 !== b && null !== b && (e.callback = b);
  b = nh(a, e, d);
  null !== b && (gi(b, a, d, c), oh(b, a, d));
} };
function Fi(a, b, c, d, e, f2, g) {
  a = a.stateNode;
  return "function" === typeof a.shouldComponentUpdate ? a.shouldComponentUpdate(d, f2, g) : b.prototype && b.prototype.isPureReactComponent ? !Ie(c, d) || !Ie(e, f2) : true;
}
function Gi(a, b, c) {
  var d = false, e = Vf;
  var f2 = b.contextType;
  "object" === typeof f2 && null !== f2 ? f2 = eh(f2) : (e = Zf(b) ? Xf : H.current, d = b.contextTypes, f2 = (d = null !== d && void 0 !== d) ? Yf(a, e) : Vf);
  b = new b(c, f2);
  a.memoizedState = null !== b.state && void 0 !== b.state ? b.state : null;
  b.updater = Ei;
  a.stateNode = b;
  b._reactInternals = a;
  d && (a = a.stateNode, a.__reactInternalMemoizedUnmaskedChildContext = e, a.__reactInternalMemoizedMaskedChildContext = f2);
  return b;
}
function Hi(a, b, c, d) {
  a = b.state;
  "function" === typeof b.componentWillReceiveProps && b.componentWillReceiveProps(c, d);
  "function" === typeof b.UNSAFE_componentWillReceiveProps && b.UNSAFE_componentWillReceiveProps(c, d);
  b.state !== a && Ei.enqueueReplaceState(b, b.state, null);
}
function Ii(a, b, c, d) {
  var e = a.stateNode;
  e.props = c;
  e.state = a.memoizedState;
  e.refs = {};
  kh(a);
  var f2 = b.contextType;
  "object" === typeof f2 && null !== f2 ? e.context = eh(f2) : (f2 = Zf(b) ? Xf : H.current, e.context = Yf(a, f2));
  e.state = a.memoizedState;
  f2 = b.getDerivedStateFromProps;
  "function" === typeof f2 && (Di(a, b, f2, c), e.state = a.memoizedState);
  "function" === typeof b.getDerivedStateFromProps || "function" === typeof e.getSnapshotBeforeUpdate || "function" !== typeof e.UNSAFE_componentWillMount && "function" !== typeof e.componentWillMount || (b = e.state, "function" === typeof e.componentWillMount && e.componentWillMount(), "function" === typeof e.UNSAFE_componentWillMount && e.UNSAFE_componentWillMount(), b !== e.state && Ei.enqueueReplaceState(e, e.state, null), qh(a, c, e, d), e.state = a.memoizedState);
  "function" === typeof e.componentDidMount && (a.flags |= 4194308);
}
function Ji(a, b) {
  try {
    var c = "", d = b;
    do
      c += Pa(d), d = d.return;
    while (d);
    var e = c;
  } catch (f2) {
    e = "\nError generating stack: " + f2.message + "\n" + f2.stack;
  }
  return { value: a, source: b, stack: e, digest: null };
}
function Ki(a, b, c) {
  return { value: a, source: null, stack: null != c ? c : null, digest: null != b ? b : null };
}
function Li(a, b) {
  try {
    console.error(b.value);
  } catch (c) {
    setTimeout(function() {
      throw c;
    });
  }
}
var Mi = "function" === typeof WeakMap ? WeakMap : Map;
function Ni(a, b, c) {
  c = mh(-1, c);
  c.tag = 3;
  c.payload = { element: null };
  var d = b.value;
  c.callback = function() {
    Oi || (Oi = true, Pi = d);
    Li(a, b);
  };
  return c;
}
function Qi(a, b, c) {
  c = mh(-1, c);
  c.tag = 3;
  var d = a.type.getDerivedStateFromError;
  if ("function" === typeof d) {
    var e = b.value;
    c.payload = function() {
      return d(e);
    };
    c.callback = function() {
      Li(a, b);
    };
  }
  var f2 = a.stateNode;
  null !== f2 && "function" === typeof f2.componentDidCatch && (c.callback = function() {
    Li(a, b);
    "function" !== typeof d && (null === Ri ? Ri = /* @__PURE__ */ new Set([this]) : Ri.add(this));
    var c2 = b.stack;
    this.componentDidCatch(b.value, { componentStack: null !== c2 ? c2 : "" });
  });
  return c;
}
function Si(a, b, c) {
  var d = a.pingCache;
  if (null === d) {
    d = a.pingCache = new Mi();
    var e = /* @__PURE__ */ new Set();
    d.set(b, e);
  } else e = d.get(b), void 0 === e && (e = /* @__PURE__ */ new Set(), d.set(b, e));
  e.has(c) || (e.add(c), a = Ti.bind(null, a, b, c), b.then(a, a));
}
function Ui(a) {
  do {
    var b;
    if (b = 13 === a.tag) b = a.memoizedState, b = null !== b ? null !== b.dehydrated ? true : false : true;
    if (b) return a;
    a = a.return;
  } while (null !== a);
  return null;
}
function Vi(a, b, c, d, e) {
  if (0 === (a.mode & 1)) return a === b ? a.flags |= 65536 : (a.flags |= 128, c.flags |= 131072, c.flags &= -52805, 1 === c.tag && (null === c.alternate ? c.tag = 17 : (b = mh(-1, 1), b.tag = 2, nh(c, b, 1))), c.lanes |= 1), a;
  a.flags |= 65536;
  a.lanes = e;
  return a;
}
var Wi = ua.ReactCurrentOwner, dh = false;
function Xi(a, b, c, d) {
  b.child = null === a ? Vg(b, null, c, d) : Ug(b, a.child, c, d);
}
function Yi(a, b, c, d, e) {
  c = c.render;
  var f2 = b.ref;
  ch(b, e);
  d = Nh(a, b, c, d, f2, e);
  c = Sh();
  if (null !== a && !dh) return b.updateQueue = a.updateQueue, b.flags &= -2053, a.lanes &= ~e, Zi(a, b, e);
  I && c && vg(b);
  b.flags |= 1;
  Xi(a, b, d, e);
  return b.child;
}
function $i(a, b, c, d, e) {
  if (null === a) {
    var f2 = c.type;
    if ("function" === typeof f2 && !aj(f2) && void 0 === f2.defaultProps && null === c.compare && void 0 === c.defaultProps) return b.tag = 15, b.type = f2, bj(a, b, f2, d, e);
    a = Rg(c.type, null, d, b, b.mode, e);
    a.ref = b.ref;
    a.return = b;
    return b.child = a;
  }
  f2 = a.child;
  if (0 === (a.lanes & e)) {
    var g = f2.memoizedProps;
    c = c.compare;
    c = null !== c ? c : Ie;
    if (c(g, d) && a.ref === b.ref) return Zi(a, b, e);
  }
  b.flags |= 1;
  a = Pg(f2, d);
  a.ref = b.ref;
  a.return = b;
  return b.child = a;
}
function bj(a, b, c, d, e) {
  if (null !== a) {
    var f2 = a.memoizedProps;
    if (Ie(f2, d) && a.ref === b.ref) if (dh = false, b.pendingProps = d = f2, 0 !== (a.lanes & e)) 0 !== (a.flags & 131072) && (dh = true);
    else return b.lanes = a.lanes, Zi(a, b, e);
  }
  return cj(a, b, c, d, e);
}
function dj(a, b, c) {
  var d = b.pendingProps, e = d.children, f2 = null !== a ? a.memoizedState : null;
  if ("hidden" === d.mode) if (0 === (b.mode & 1)) b.memoizedState = { baseLanes: 0, cachePool: null, transitions: null }, G(ej, fj), fj |= c;
  else {
    if (0 === (c & 1073741824)) return a = null !== f2 ? f2.baseLanes | c : c, b.lanes = b.childLanes = 1073741824, b.memoizedState = { baseLanes: a, cachePool: null, transitions: null }, b.updateQueue = null, G(ej, fj), fj |= a, null;
    b.memoizedState = { baseLanes: 0, cachePool: null, transitions: null };
    d = null !== f2 ? f2.baseLanes : c;
    G(ej, fj);
    fj |= d;
  }
  else null !== f2 ? (d = f2.baseLanes | c, b.memoizedState = null) : d = c, G(ej, fj), fj |= d;
  Xi(a, b, e, c);
  return b.child;
}
function gj(a, b) {
  var c = b.ref;
  if (null === a && null !== c || null !== a && a.ref !== c) b.flags |= 512, b.flags |= 2097152;
}
function cj(a, b, c, d, e) {
  var f2 = Zf(c) ? Xf : H.current;
  f2 = Yf(b, f2);
  ch(b, e);
  c = Nh(a, b, c, d, f2, e);
  d = Sh();
  if (null !== a && !dh) return b.updateQueue = a.updateQueue, b.flags &= -2053, a.lanes &= ~e, Zi(a, b, e);
  I && d && vg(b);
  b.flags |= 1;
  Xi(a, b, c, e);
  return b.child;
}
function hj(a, b, c, d, e) {
  if (Zf(c)) {
    var f2 = true;
    cg(b);
  } else f2 = false;
  ch(b, e);
  if (null === b.stateNode) ij(a, b), Gi(b, c, d), Ii(b, c, d, e), d = true;
  else if (null === a) {
    var g = b.stateNode, h = b.memoizedProps;
    g.props = h;
    var k2 = g.context, l2 = c.contextType;
    "object" === typeof l2 && null !== l2 ? l2 = eh(l2) : (l2 = Zf(c) ? Xf : H.current, l2 = Yf(b, l2));
    var m2 = c.getDerivedStateFromProps, q2 = "function" === typeof m2 || "function" === typeof g.getSnapshotBeforeUpdate;
    q2 || "function" !== typeof g.UNSAFE_componentWillReceiveProps && "function" !== typeof g.componentWillReceiveProps || (h !== d || k2 !== l2) && Hi(b, g, d, l2);
    jh = false;
    var r2 = b.memoizedState;
    g.state = r2;
    qh(b, d, g, e);
    k2 = b.memoizedState;
    h !== d || r2 !== k2 || Wf.current || jh ? ("function" === typeof m2 && (Di(b, c, m2, d), k2 = b.memoizedState), (h = jh || Fi(b, c, h, d, r2, k2, l2)) ? (q2 || "function" !== typeof g.UNSAFE_componentWillMount && "function" !== typeof g.componentWillMount || ("function" === typeof g.componentWillMount && g.componentWillMount(), "function" === typeof g.UNSAFE_componentWillMount && g.UNSAFE_componentWillMount()), "function" === typeof g.componentDidMount && (b.flags |= 4194308)) : ("function" === typeof g.componentDidMount && (b.flags |= 4194308), b.memoizedProps = d, b.memoizedState = k2), g.props = d, g.state = k2, g.context = l2, d = h) : ("function" === typeof g.componentDidMount && (b.flags |= 4194308), d = false);
  } else {
    g = b.stateNode;
    lh(a, b);
    h = b.memoizedProps;
    l2 = b.type === b.elementType ? h : Ci(b.type, h);
    g.props = l2;
    q2 = b.pendingProps;
    r2 = g.context;
    k2 = c.contextType;
    "object" === typeof k2 && null !== k2 ? k2 = eh(k2) : (k2 = Zf(c) ? Xf : H.current, k2 = Yf(b, k2));
    var y2 = c.getDerivedStateFromProps;
    (m2 = "function" === typeof y2 || "function" === typeof g.getSnapshotBeforeUpdate) || "function" !== typeof g.UNSAFE_componentWillReceiveProps && "function" !== typeof g.componentWillReceiveProps || (h !== q2 || r2 !== k2) && Hi(b, g, d, k2);
    jh = false;
    r2 = b.memoizedState;
    g.state = r2;
    qh(b, d, g, e);
    var n2 = b.memoizedState;
    h !== q2 || r2 !== n2 || Wf.current || jh ? ("function" === typeof y2 && (Di(b, c, y2, d), n2 = b.memoizedState), (l2 = jh || Fi(b, c, l2, d, r2, n2, k2) || false) ? (m2 || "function" !== typeof g.UNSAFE_componentWillUpdate && "function" !== typeof g.componentWillUpdate || ("function" === typeof g.componentWillUpdate && g.componentWillUpdate(d, n2, k2), "function" === typeof g.UNSAFE_componentWillUpdate && g.UNSAFE_componentWillUpdate(d, n2, k2)), "function" === typeof g.componentDidUpdate && (b.flags |= 4), "function" === typeof g.getSnapshotBeforeUpdate && (b.flags |= 1024)) : ("function" !== typeof g.componentDidUpdate || h === a.memoizedProps && r2 === a.memoizedState || (b.flags |= 4), "function" !== typeof g.getSnapshotBeforeUpdate || h === a.memoizedProps && r2 === a.memoizedState || (b.flags |= 1024), b.memoizedProps = d, b.memoizedState = n2), g.props = d, g.state = n2, g.context = k2, d = l2) : ("function" !== typeof g.componentDidUpdate || h === a.memoizedProps && r2 === a.memoizedState || (b.flags |= 4), "function" !== typeof g.getSnapshotBeforeUpdate || h === a.memoizedProps && r2 === a.memoizedState || (b.flags |= 1024), d = false);
  }
  return jj(a, b, c, d, f2, e);
}
function jj(a, b, c, d, e, f2) {
  gj(a, b);
  var g = 0 !== (b.flags & 128);
  if (!d && !g) return e && dg(b, c, false), Zi(a, b, f2);
  d = b.stateNode;
  Wi.current = b;
  var h = g && "function" !== typeof c.getDerivedStateFromError ? null : d.render();
  b.flags |= 1;
  null !== a && g ? (b.child = Ug(b, a.child, null, f2), b.child = Ug(b, null, h, f2)) : Xi(a, b, h, f2);
  b.memoizedState = d.state;
  e && dg(b, c, true);
  return b.child;
}
function kj(a) {
  var b = a.stateNode;
  b.pendingContext ? ag(a, b.pendingContext, b.pendingContext !== b.context) : b.context && ag(a, b.context, false);
  yh(a, b.containerInfo);
}
function lj(a, b, c, d, e) {
  Ig();
  Jg(e);
  b.flags |= 256;
  Xi(a, b, c, d);
  return b.child;
}
var mj = { dehydrated: null, treeContext: null, retryLane: 0 };
function nj(a) {
  return { baseLanes: a, cachePool: null, transitions: null };
}
function oj(a, b, c) {
  var d = b.pendingProps, e = L.current, f2 = false, g = 0 !== (b.flags & 128), h;
  (h = g) || (h = null !== a && null === a.memoizedState ? false : 0 !== (e & 2));
  if (h) f2 = true, b.flags &= -129;
  else if (null === a || null !== a.memoizedState) e |= 1;
  G(L, e & 1);
  if (null === a) {
    Eg(b);
    a = b.memoizedState;
    if (null !== a && (a = a.dehydrated, null !== a)) return 0 === (b.mode & 1) ? b.lanes = 1 : "$!" === a.data ? b.lanes = 8 : b.lanes = 1073741824, null;
    g = d.children;
    a = d.fallback;
    return f2 ? (d = b.mode, f2 = b.child, g = { mode: "hidden", children: g }, 0 === (d & 1) && null !== f2 ? (f2.childLanes = 0, f2.pendingProps = g) : f2 = pj(g, d, 0, null), a = Tg(a, d, c, null), f2.return = b, a.return = b, f2.sibling = a, b.child = f2, b.child.memoizedState = nj(c), b.memoizedState = mj, a) : qj(b, g);
  }
  e = a.memoizedState;
  if (null !== e && (h = e.dehydrated, null !== h)) return rj(a, b, g, d, h, e, c);
  if (f2) {
    f2 = d.fallback;
    g = b.mode;
    e = a.child;
    h = e.sibling;
    var k2 = { mode: "hidden", children: d.children };
    0 === (g & 1) && b.child !== e ? (d = b.child, d.childLanes = 0, d.pendingProps = k2, b.deletions = null) : (d = Pg(e, k2), d.subtreeFlags = e.subtreeFlags & 14680064);
    null !== h ? f2 = Pg(h, f2) : (f2 = Tg(f2, g, c, null), f2.flags |= 2);
    f2.return = b;
    d.return = b;
    d.sibling = f2;
    b.child = d;
    d = f2;
    f2 = b.child;
    g = a.child.memoizedState;
    g = null === g ? nj(c) : { baseLanes: g.baseLanes | c, cachePool: null, transitions: g.transitions };
    f2.memoizedState = g;
    f2.childLanes = a.childLanes & ~c;
    b.memoizedState = mj;
    return d;
  }
  f2 = a.child;
  a = f2.sibling;
  d = Pg(f2, { mode: "visible", children: d.children });
  0 === (b.mode & 1) && (d.lanes = c);
  d.return = b;
  d.sibling = null;
  null !== a && (c = b.deletions, null === c ? (b.deletions = [a], b.flags |= 16) : c.push(a));
  b.child = d;
  b.memoizedState = null;
  return d;
}
function qj(a, b) {
  b = pj({ mode: "visible", children: b }, a.mode, 0, null);
  b.return = a;
  return a.child = b;
}
function sj(a, b, c, d) {
  null !== d && Jg(d);
  Ug(b, a.child, null, c);
  a = qj(b, b.pendingProps.children);
  a.flags |= 2;
  b.memoizedState = null;
  return a;
}
function rj(a, b, c, d, e, f2, g) {
  if (c) {
    if (b.flags & 256) return b.flags &= -257, d = Ki(Error(p(422))), sj(a, b, g, d);
    if (null !== b.memoizedState) return b.child = a.child, b.flags |= 128, null;
    f2 = d.fallback;
    e = b.mode;
    d = pj({ mode: "visible", children: d.children }, e, 0, null);
    f2 = Tg(f2, e, g, null);
    f2.flags |= 2;
    d.return = b;
    f2.return = b;
    d.sibling = f2;
    b.child = d;
    0 !== (b.mode & 1) && Ug(b, a.child, null, g);
    b.child.memoizedState = nj(g);
    b.memoizedState = mj;
    return f2;
  }
  if (0 === (b.mode & 1)) return sj(a, b, g, null);
  if ("$!" === e.data) {
    d = e.nextSibling && e.nextSibling.dataset;
    if (d) var h = d.dgst;
    d = h;
    f2 = Error(p(419));
    d = Ki(f2, d, void 0);
    return sj(a, b, g, d);
  }
  h = 0 !== (g & a.childLanes);
  if (dh || h) {
    d = Q;
    if (null !== d) {
      switch (g & -g) {
        case 4:
          e = 2;
          break;
        case 16:
          e = 8;
          break;
        case 64:
        case 128:
        case 256:
        case 512:
        case 1024:
        case 2048:
        case 4096:
        case 8192:
        case 16384:
        case 32768:
        case 65536:
        case 131072:
        case 262144:
        case 524288:
        case 1048576:
        case 2097152:
        case 4194304:
        case 8388608:
        case 16777216:
        case 33554432:
        case 67108864:
          e = 32;
          break;
        case 536870912:
          e = 268435456;
          break;
        default:
          e = 0;
      }
      e = 0 !== (e & (d.suspendedLanes | g)) ? 0 : e;
      0 !== e && e !== f2.retryLane && (f2.retryLane = e, ih(a, e), gi(d, a, e, -1));
    }
    tj();
    d = Ki(Error(p(421)));
    return sj(a, b, g, d);
  }
  if ("$?" === e.data) return b.flags |= 128, b.child = a.child, b = uj.bind(null, a), e._reactRetry = b, null;
  a = f2.treeContext;
  yg = Lf(e.nextSibling);
  xg = b;
  I = true;
  zg = null;
  null !== a && (og[pg++] = rg, og[pg++] = sg, og[pg++] = qg, rg = a.id, sg = a.overflow, qg = b);
  b = qj(b, d.children);
  b.flags |= 4096;
  return b;
}
function vj(a, b, c) {
  a.lanes |= b;
  var d = a.alternate;
  null !== d && (d.lanes |= b);
  bh(a.return, b, c);
}
function wj(a, b, c, d, e) {
  var f2 = a.memoizedState;
  null === f2 ? a.memoizedState = { isBackwards: b, rendering: null, renderingStartTime: 0, last: d, tail: c, tailMode: e } : (f2.isBackwards = b, f2.rendering = null, f2.renderingStartTime = 0, f2.last = d, f2.tail = c, f2.tailMode = e);
}
function xj(a, b, c) {
  var d = b.pendingProps, e = d.revealOrder, f2 = d.tail;
  Xi(a, b, d.children, c);
  d = L.current;
  if (0 !== (d & 2)) d = d & 1 | 2, b.flags |= 128;
  else {
    if (null !== a && 0 !== (a.flags & 128)) a: for (a = b.child; null !== a; ) {
      if (13 === a.tag) null !== a.memoizedState && vj(a, c, b);
      else if (19 === a.tag) vj(a, c, b);
      else if (null !== a.child) {
        a.child.return = a;
        a = a.child;
        continue;
      }
      if (a === b) break a;
      for (; null === a.sibling; ) {
        if (null === a.return || a.return === b) break a;
        a = a.return;
      }
      a.sibling.return = a.return;
      a = a.sibling;
    }
    d &= 1;
  }
  G(L, d);
  if (0 === (b.mode & 1)) b.memoizedState = null;
  else switch (e) {
    case "forwards":
      c = b.child;
      for (e = null; null !== c; ) a = c.alternate, null !== a && null === Ch(a) && (e = c), c = c.sibling;
      c = e;
      null === c ? (e = b.child, b.child = null) : (e = c.sibling, c.sibling = null);
      wj(b, false, e, c, f2);
      break;
    case "backwards":
      c = null;
      e = b.child;
      for (b.child = null; null !== e; ) {
        a = e.alternate;
        if (null !== a && null === Ch(a)) {
          b.child = e;
          break;
        }
        a = e.sibling;
        e.sibling = c;
        c = e;
        e = a;
      }
      wj(b, true, c, null, f2);
      break;
    case "together":
      wj(b, false, null, null, void 0);
      break;
    default:
      b.memoizedState = null;
  }
  return b.child;
}
function ij(a, b) {
  0 === (b.mode & 1) && null !== a && (a.alternate = null, b.alternate = null, b.flags |= 2);
}
function Zi(a, b, c) {
  null !== a && (b.dependencies = a.dependencies);
  rh |= b.lanes;
  if (0 === (c & b.childLanes)) return null;
  if (null !== a && b.child !== a.child) throw Error(p(153));
  if (null !== b.child) {
    a = b.child;
    c = Pg(a, a.pendingProps);
    b.child = c;
    for (c.return = b; null !== a.sibling; ) a = a.sibling, c = c.sibling = Pg(a, a.pendingProps), c.return = b;
    c.sibling = null;
  }
  return b.child;
}
function yj(a, b, c) {
  switch (b.tag) {
    case 3:
      kj(b);
      Ig();
      break;
    case 5:
      Ah(b);
      break;
    case 1:
      Zf(b.type) && cg(b);
      break;
    case 4:
      yh(b, b.stateNode.containerInfo);
      break;
    case 10:
      var d = b.type._context, e = b.memoizedProps.value;
      G(Wg, d._currentValue);
      d._currentValue = e;
      break;
    case 13:
      d = b.memoizedState;
      if (null !== d) {
        if (null !== d.dehydrated) return G(L, L.current & 1), b.flags |= 128, null;
        if (0 !== (c & b.child.childLanes)) return oj(a, b, c);
        G(L, L.current & 1);
        a = Zi(a, b, c);
        return null !== a ? a.sibling : null;
      }
      G(L, L.current & 1);
      break;
    case 19:
      d = 0 !== (c & b.childLanes);
      if (0 !== (a.flags & 128)) {
        if (d) return xj(a, b, c);
        b.flags |= 128;
      }
      e = b.memoizedState;
      null !== e && (e.rendering = null, e.tail = null, e.lastEffect = null);
      G(L, L.current);
      if (d) break;
      else return null;
    case 22:
    case 23:
      return b.lanes = 0, dj(a, b, c);
  }
  return Zi(a, b, c);
}
var zj, Aj, Bj, Cj;
zj = function(a, b) {
  for (var c = b.child; null !== c; ) {
    if (5 === c.tag || 6 === c.tag) a.appendChild(c.stateNode);
    else if (4 !== c.tag && null !== c.child) {
      c.child.return = c;
      c = c.child;
      continue;
    }
    if (c === b) break;
    for (; null === c.sibling; ) {
      if (null === c.return || c.return === b) return;
      c = c.return;
    }
    c.sibling.return = c.return;
    c = c.sibling;
  }
};
Aj = function() {
};
Bj = function(a, b, c, d) {
  var e = a.memoizedProps;
  if (e !== d) {
    a = b.stateNode;
    xh(uh.current);
    var f2 = null;
    switch (c) {
      case "input":
        e = Ya(a, e);
        d = Ya(a, d);
        f2 = [];
        break;
      case "select":
        e = A({}, e, { value: void 0 });
        d = A({}, d, { value: void 0 });
        f2 = [];
        break;
      case "textarea":
        e = gb(a, e);
        d = gb(a, d);
        f2 = [];
        break;
      default:
        "function" !== typeof e.onClick && "function" === typeof d.onClick && (a.onclick = Bf);
    }
    ub(c, d);
    var g;
    c = null;
    for (l2 in e) if (!d.hasOwnProperty(l2) && e.hasOwnProperty(l2) && null != e[l2]) if ("style" === l2) {
      var h = e[l2];
      for (g in h) h.hasOwnProperty(g) && (c || (c = {}), c[g] = "");
    } else "dangerouslySetInnerHTML" !== l2 && "children" !== l2 && "suppressContentEditableWarning" !== l2 && "suppressHydrationWarning" !== l2 && "autoFocus" !== l2 && (ea.hasOwnProperty(l2) ? f2 || (f2 = []) : (f2 = f2 || []).push(l2, null));
    for (l2 in d) {
      var k2 = d[l2];
      h = null != e ? e[l2] : void 0;
      if (d.hasOwnProperty(l2) && k2 !== h && (null != k2 || null != h)) if ("style" === l2) if (h) {
        for (g in h) !h.hasOwnProperty(g) || k2 && k2.hasOwnProperty(g) || (c || (c = {}), c[g] = "");
        for (g in k2) k2.hasOwnProperty(g) && h[g] !== k2[g] && (c || (c = {}), c[g] = k2[g]);
      } else c || (f2 || (f2 = []), f2.push(
        l2,
        c
      )), c = k2;
      else "dangerouslySetInnerHTML" === l2 ? (k2 = k2 ? k2.__html : void 0, h = h ? h.__html : void 0, null != k2 && h !== k2 && (f2 = f2 || []).push(l2, k2)) : "children" === l2 ? "string" !== typeof k2 && "number" !== typeof k2 || (f2 = f2 || []).push(l2, "" + k2) : "suppressContentEditableWarning" !== l2 && "suppressHydrationWarning" !== l2 && (ea.hasOwnProperty(l2) ? (null != k2 && "onScroll" === l2 && D("scroll", a), f2 || h === k2 || (f2 = [])) : (f2 = f2 || []).push(l2, k2));
    }
    c && (f2 = f2 || []).push("style", c);
    var l2 = f2;
    if (b.updateQueue = l2) b.flags |= 4;
  }
};
Cj = function(a, b, c, d) {
  c !== d && (b.flags |= 4);
};
function Dj(a, b) {
  if (!I) switch (a.tailMode) {
    case "hidden":
      b = a.tail;
      for (var c = null; null !== b; ) null !== b.alternate && (c = b), b = b.sibling;
      null === c ? a.tail = null : c.sibling = null;
      break;
    case "collapsed":
      c = a.tail;
      for (var d = null; null !== c; ) null !== c.alternate && (d = c), c = c.sibling;
      null === d ? b || null === a.tail ? a.tail = null : a.tail.sibling = null : d.sibling = null;
  }
}
function S(a) {
  var b = null !== a.alternate && a.alternate.child === a.child, c = 0, d = 0;
  if (b) for (var e = a.child; null !== e; ) c |= e.lanes | e.childLanes, d |= e.subtreeFlags & 14680064, d |= e.flags & 14680064, e.return = a, e = e.sibling;
  else for (e = a.child; null !== e; ) c |= e.lanes | e.childLanes, d |= e.subtreeFlags, d |= e.flags, e.return = a, e = e.sibling;
  a.subtreeFlags |= d;
  a.childLanes = c;
  return b;
}
function Ej(a, b, c) {
  var d = b.pendingProps;
  wg(b);
  switch (b.tag) {
    case 2:
    case 16:
    case 15:
    case 0:
    case 11:
    case 7:
    case 8:
    case 12:
    case 9:
    case 14:
      return S(b), null;
    case 1:
      return Zf(b.type) && $f(), S(b), null;
    case 3:
      d = b.stateNode;
      zh();
      E(Wf);
      E(H);
      Eh();
      d.pendingContext && (d.context = d.pendingContext, d.pendingContext = null);
      if (null === a || null === a.child) Gg(b) ? b.flags |= 4 : null === a || a.memoizedState.isDehydrated && 0 === (b.flags & 256) || (b.flags |= 1024, null !== zg && (Fj(zg), zg = null));
      Aj(a, b);
      S(b);
      return null;
    case 5:
      Bh(b);
      var e = xh(wh.current);
      c = b.type;
      if (null !== a && null != b.stateNode) Bj(a, b, c, d, e), a.ref !== b.ref && (b.flags |= 512, b.flags |= 2097152);
      else {
        if (!d) {
          if (null === b.stateNode) throw Error(p(166));
          S(b);
          return null;
        }
        a = xh(uh.current);
        if (Gg(b)) {
          d = b.stateNode;
          c = b.type;
          var f2 = b.memoizedProps;
          d[Of] = b;
          d[Pf] = f2;
          a = 0 !== (b.mode & 1);
          switch (c) {
            case "dialog":
              D("cancel", d);
              D("close", d);
              break;
            case "iframe":
            case "object":
            case "embed":
              D("load", d);
              break;
            case "video":
            case "audio":
              for (e = 0; e < lf.length; e++) D(lf[e], d);
              break;
            case "source":
              D("error", d);
              break;
            case "img":
            case "image":
            case "link":
              D(
                "error",
                d
              );
              D("load", d);
              break;
            case "details":
              D("toggle", d);
              break;
            case "input":
              Za(d, f2);
              D("invalid", d);
              break;
            case "select":
              d._wrapperState = { wasMultiple: !!f2.multiple };
              D("invalid", d);
              break;
            case "textarea":
              hb(d, f2), D("invalid", d);
          }
          ub(c, f2);
          e = null;
          for (var g in f2) if (f2.hasOwnProperty(g)) {
            var h = f2[g];
            "children" === g ? "string" === typeof h ? d.textContent !== h && (true !== f2.suppressHydrationWarning && Af(d.textContent, h, a), e = ["children", h]) : "number" === typeof h && d.textContent !== "" + h && (true !== f2.suppressHydrationWarning && Af(
              d.textContent,
              h,
              a
            ), e = ["children", "" + h]) : ea.hasOwnProperty(g) && null != h && "onScroll" === g && D("scroll", d);
          }
          switch (c) {
            case "input":
              Va(d);
              db(d, f2, true);
              break;
            case "textarea":
              Va(d);
              jb(d);
              break;
            case "select":
            case "option":
              break;
            default:
              "function" === typeof f2.onClick && (d.onclick = Bf);
          }
          d = e;
          b.updateQueue = d;
          null !== d && (b.flags |= 4);
        } else {
          g = 9 === e.nodeType ? e : e.ownerDocument;
          "http://www.w3.org/1999/xhtml" === a && (a = kb(c));
          "http://www.w3.org/1999/xhtml" === a ? "script" === c ? (a = g.createElement("div"), a.innerHTML = "<script><\/script>", a = a.removeChild(a.firstChild)) : "string" === typeof d.is ? a = g.createElement(c, { is: d.is }) : (a = g.createElement(c), "select" === c && (g = a, d.multiple ? g.multiple = true : d.size && (g.size = d.size))) : a = g.createElementNS(a, c);
          a[Of] = b;
          a[Pf] = d;
          zj(a, b, false, false);
          b.stateNode = a;
          a: {
            g = vb(c, d);
            switch (c) {
              case "dialog":
                D("cancel", a);
                D("close", a);
                e = d;
                break;
              case "iframe":
              case "object":
              case "embed":
                D("load", a);
                e = d;
                break;
              case "video":
              case "audio":
                for (e = 0; e < lf.length; e++) D(lf[e], a);
                e = d;
                break;
              case "source":
                D("error", a);
                e = d;
                break;
              case "img":
              case "image":
              case "link":
                D(
                  "error",
                  a
                );
                D("load", a);
                e = d;
                break;
              case "details":
                D("toggle", a);
                e = d;
                break;
              case "input":
                Za(a, d);
                e = Ya(a, d);
                D("invalid", a);
                break;
              case "option":
                e = d;
                break;
              case "select":
                a._wrapperState = { wasMultiple: !!d.multiple };
                e = A({}, d, { value: void 0 });
                D("invalid", a);
                break;
              case "textarea":
                hb(a, d);
                e = gb(a, d);
                D("invalid", a);
                break;
              default:
                e = d;
            }
            ub(c, e);
            h = e;
            for (f2 in h) if (h.hasOwnProperty(f2)) {
              var k2 = h[f2];
              "style" === f2 ? sb(a, k2) : "dangerouslySetInnerHTML" === f2 ? (k2 = k2 ? k2.__html : void 0, null != k2 && nb(a, k2)) : "children" === f2 ? "string" === typeof k2 ? ("textarea" !== c || "" !== k2) && ob(a, k2) : "number" === typeof k2 && ob(a, "" + k2) : "suppressContentEditableWarning" !== f2 && "suppressHydrationWarning" !== f2 && "autoFocus" !== f2 && (ea.hasOwnProperty(f2) ? null != k2 && "onScroll" === f2 && D("scroll", a) : null != k2 && ta(a, f2, k2, g));
            }
            switch (c) {
              case "input":
                Va(a);
                db(a, d, false);
                break;
              case "textarea":
                Va(a);
                jb(a);
                break;
              case "option":
                null != d.value && a.setAttribute("value", "" + Sa(d.value));
                break;
              case "select":
                a.multiple = !!d.multiple;
                f2 = d.value;
                null != f2 ? fb(a, !!d.multiple, f2, false) : null != d.defaultValue && fb(
                  a,
                  !!d.multiple,
                  d.defaultValue,
                  true
                );
                break;
              default:
                "function" === typeof e.onClick && (a.onclick = Bf);
            }
            switch (c) {
              case "button":
              case "input":
              case "select":
              case "textarea":
                d = !!d.autoFocus;
                break a;
              case "img":
                d = true;
                break a;
              default:
                d = false;
            }
          }
          d && (b.flags |= 4);
        }
        null !== b.ref && (b.flags |= 512, b.flags |= 2097152);
      }
      S(b);
      return null;
    case 6:
      if (a && null != b.stateNode) Cj(a, b, a.memoizedProps, d);
      else {
        if ("string" !== typeof d && null === b.stateNode) throw Error(p(166));
        c = xh(wh.current);
        xh(uh.current);
        if (Gg(b)) {
          d = b.stateNode;
          c = b.memoizedProps;
          d[Of] = b;
          if (f2 = d.nodeValue !== c) {
            if (a = xg, null !== a) switch (a.tag) {
              case 3:
                Af(d.nodeValue, c, 0 !== (a.mode & 1));
                break;
              case 5:
                true !== a.memoizedProps.suppressHydrationWarning && Af(d.nodeValue, c, 0 !== (a.mode & 1));
            }
          }
          f2 && (b.flags |= 4);
        } else d = (9 === c.nodeType ? c : c.ownerDocument).createTextNode(d), d[Of] = b, b.stateNode = d;
      }
      S(b);
      return null;
    case 13:
      E(L);
      d = b.memoizedState;
      if (null === a || null !== a.memoizedState && null !== a.memoizedState.dehydrated) {
        if (I && null !== yg && 0 !== (b.mode & 1) && 0 === (b.flags & 128)) Hg(), Ig(), b.flags |= 98560, f2 = false;
        else if (f2 = Gg(b), null !== d && null !== d.dehydrated) {
          if (null === a) {
            if (!f2) throw Error(p(318));
            f2 = b.memoizedState;
            f2 = null !== f2 ? f2.dehydrated : null;
            if (!f2) throw Error(p(317));
            f2[Of] = b;
          } else Ig(), 0 === (b.flags & 128) && (b.memoizedState = null), b.flags |= 4;
          S(b);
          f2 = false;
        } else null !== zg && (Fj(zg), zg = null), f2 = true;
        if (!f2) return b.flags & 65536 ? b : null;
      }
      if (0 !== (b.flags & 128)) return b.lanes = c, b;
      d = null !== d;
      d !== (null !== a && null !== a.memoizedState) && d && (b.child.flags |= 8192, 0 !== (b.mode & 1) && (null === a || 0 !== (L.current & 1) ? 0 === T && (T = 3) : tj()));
      null !== b.updateQueue && (b.flags |= 4);
      S(b);
      return null;
    case 4:
      return zh(), Aj(a, b), null === a && sf(b.stateNode.containerInfo), S(b), null;
    case 10:
      return ah(b.type._context), S(b), null;
    case 17:
      return Zf(b.type) && $f(), S(b), null;
    case 19:
      E(L);
      f2 = b.memoizedState;
      if (null === f2) return S(b), null;
      d = 0 !== (b.flags & 128);
      g = f2.rendering;
      if (null === g) if (d) Dj(f2, false);
      else {
        if (0 !== T || null !== a && 0 !== (a.flags & 128)) for (a = b.child; null !== a; ) {
          g = Ch(a);
          if (null !== g) {
            b.flags |= 128;
            Dj(f2, false);
            d = g.updateQueue;
            null !== d && (b.updateQueue = d, b.flags |= 4);
            b.subtreeFlags = 0;
            d = c;
            for (c = b.child; null !== c; ) f2 = c, a = d, f2.flags &= 14680066, g = f2.alternate, null === g ? (f2.childLanes = 0, f2.lanes = a, f2.child = null, f2.subtreeFlags = 0, f2.memoizedProps = null, f2.memoizedState = null, f2.updateQueue = null, f2.dependencies = null, f2.stateNode = null) : (f2.childLanes = g.childLanes, f2.lanes = g.lanes, f2.child = g.child, f2.subtreeFlags = 0, f2.deletions = null, f2.memoizedProps = g.memoizedProps, f2.memoizedState = g.memoizedState, f2.updateQueue = g.updateQueue, f2.type = g.type, a = g.dependencies, f2.dependencies = null === a ? null : { lanes: a.lanes, firstContext: a.firstContext }), c = c.sibling;
            G(L, L.current & 1 | 2);
            return b.child;
          }
          a = a.sibling;
        }
        null !== f2.tail && B() > Gj && (b.flags |= 128, d = true, Dj(f2, false), b.lanes = 4194304);
      }
      else {
        if (!d) if (a = Ch(g), null !== a) {
          if (b.flags |= 128, d = true, c = a.updateQueue, null !== c && (b.updateQueue = c, b.flags |= 4), Dj(f2, true), null === f2.tail && "hidden" === f2.tailMode && !g.alternate && !I) return S(b), null;
        } else 2 * B() - f2.renderingStartTime > Gj && 1073741824 !== c && (b.flags |= 128, d = true, Dj(f2, false), b.lanes = 4194304);
        f2.isBackwards ? (g.sibling = b.child, b.child = g) : (c = f2.last, null !== c ? c.sibling = g : b.child = g, f2.last = g);
      }
      if (null !== f2.tail) return b = f2.tail, f2.rendering = b, f2.tail = b.sibling, f2.renderingStartTime = B(), b.sibling = null, c = L.current, G(L, d ? c & 1 | 2 : c & 1), b;
      S(b);
      return null;
    case 22:
    case 23:
      return Hj(), d = null !== b.memoizedState, null !== a && null !== a.memoizedState !== d && (b.flags |= 8192), d && 0 !== (b.mode & 1) ? 0 !== (fj & 1073741824) && (S(b), b.subtreeFlags & 6 && (b.flags |= 8192)) : S(b), null;
    case 24:
      return null;
    case 25:
      return null;
  }
  throw Error(p(156, b.tag));
}
function Ij(a, b) {
  wg(b);
  switch (b.tag) {
    case 1:
      return Zf(b.type) && $f(), a = b.flags, a & 65536 ? (b.flags = a & -65537 | 128, b) : null;
    case 3:
      return zh(), E(Wf), E(H), Eh(), a = b.flags, 0 !== (a & 65536) && 0 === (a & 128) ? (b.flags = a & -65537 | 128, b) : null;
    case 5:
      return Bh(b), null;
    case 13:
      E(L);
      a = b.memoizedState;
      if (null !== a && null !== a.dehydrated) {
        if (null === b.alternate) throw Error(p(340));
        Ig();
      }
      a = b.flags;
      return a & 65536 ? (b.flags = a & -65537 | 128, b) : null;
    case 19:
      return E(L), null;
    case 4:
      return zh(), null;
    case 10:
      return ah(b.type._context), null;
    case 22:
    case 23:
      return Hj(), null;
    case 24:
      return null;
    default:
      return null;
  }
}
var Jj = false, U = false, Kj = "function" === typeof WeakSet ? WeakSet : Set, V = null;
function Lj(a, b) {
  var c = a.ref;
  if (null !== c) if ("function" === typeof c) try {
    c(null);
  } catch (d) {
    W(a, b, d);
  }
  else c.current = null;
}
function Mj(a, b, c) {
  try {
    c();
  } catch (d) {
    W(a, b, d);
  }
}
var Nj = false;
function Oj(a, b) {
  Cf = dd;
  a = Me();
  if (Ne(a)) {
    if ("selectionStart" in a) var c = { start: a.selectionStart, end: a.selectionEnd };
    else a: {
      c = (c = a.ownerDocument) && c.defaultView || window;
      var d = c.getSelection && c.getSelection();
      if (d && 0 !== d.rangeCount) {
        c = d.anchorNode;
        var e = d.anchorOffset, f2 = d.focusNode;
        d = d.focusOffset;
        try {
          c.nodeType, f2.nodeType;
        } catch (F2) {
          c = null;
          break a;
        }
        var g = 0, h = -1, k2 = -1, l2 = 0, m2 = 0, q2 = a, r2 = null;
        b: for (; ; ) {
          for (var y2; ; ) {
            q2 !== c || 0 !== e && 3 !== q2.nodeType || (h = g + e);
            q2 !== f2 || 0 !== d && 3 !== q2.nodeType || (k2 = g + d);
            3 === q2.nodeType && (g += q2.nodeValue.length);
            if (null === (y2 = q2.firstChild)) break;
            r2 = q2;
            q2 = y2;
          }
          for (; ; ) {
            if (q2 === a) break b;
            r2 === c && ++l2 === e && (h = g);
            r2 === f2 && ++m2 === d && (k2 = g);
            if (null !== (y2 = q2.nextSibling)) break;
            q2 = r2;
            r2 = q2.parentNode;
          }
          q2 = y2;
        }
        c = -1 === h || -1 === k2 ? null : { start: h, end: k2 };
      } else c = null;
    }
    c = c || { start: 0, end: 0 };
  } else c = null;
  Df = { focusedElem: a, selectionRange: c };
  dd = false;
  for (V = b; null !== V; ) if (b = V, a = b.child, 0 !== (b.subtreeFlags & 1028) && null !== a) a.return = b, V = a;
  else for (; null !== V; ) {
    b = V;
    try {
      var n2 = b.alternate;
      if (0 !== (b.flags & 1024)) switch (b.tag) {
        case 0:
        case 11:
        case 15:
          break;
        case 1:
          if (null !== n2) {
            var t2 = n2.memoizedProps, J2 = n2.memoizedState, x2 = b.stateNode, w2 = x2.getSnapshotBeforeUpdate(b.elementType === b.type ? t2 : Ci(b.type, t2), J2);
            x2.__reactInternalSnapshotBeforeUpdate = w2;
          }
          break;
        case 3:
          var u2 = b.stateNode.containerInfo;
          1 === u2.nodeType ? u2.textContent = "" : 9 === u2.nodeType && u2.documentElement && u2.removeChild(u2.documentElement);
          break;
        case 5:
        case 6:
        case 4:
        case 17:
          break;
        default:
          throw Error(p(163));
      }
    } catch (F2) {
      W(b, b.return, F2);
    }
    a = b.sibling;
    if (null !== a) {
      a.return = b.return;
      V = a;
      break;
    }
    V = b.return;
  }
  n2 = Nj;
  Nj = false;
  return n2;
}
function Pj(a, b, c) {
  var d = b.updateQueue;
  d = null !== d ? d.lastEffect : null;
  if (null !== d) {
    var e = d = d.next;
    do {
      if ((e.tag & a) === a) {
        var f2 = e.destroy;
        e.destroy = void 0;
        void 0 !== f2 && Mj(b, c, f2);
      }
      e = e.next;
    } while (e !== d);
  }
}
function Qj(a, b) {
  b = b.updateQueue;
  b = null !== b ? b.lastEffect : null;
  if (null !== b) {
    var c = b = b.next;
    do {
      if ((c.tag & a) === a) {
        var d = c.create;
        c.destroy = d();
      }
      c = c.next;
    } while (c !== b);
  }
}
function Rj(a) {
  var b = a.ref;
  if (null !== b) {
    var c = a.stateNode;
    switch (a.tag) {
      case 5:
        a = c;
        break;
      default:
        a = c;
    }
    "function" === typeof b ? b(a) : b.current = a;
  }
}
function Sj(a) {
  var b = a.alternate;
  null !== b && (a.alternate = null, Sj(b));
  a.child = null;
  a.deletions = null;
  a.sibling = null;
  5 === a.tag && (b = a.stateNode, null !== b && (delete b[Of], delete b[Pf], delete b[of], delete b[Qf], delete b[Rf]));
  a.stateNode = null;
  a.return = null;
  a.dependencies = null;
  a.memoizedProps = null;
  a.memoizedState = null;
  a.pendingProps = null;
  a.stateNode = null;
  a.updateQueue = null;
}
function Tj(a) {
  return 5 === a.tag || 3 === a.tag || 4 === a.tag;
}
function Uj(a) {
  a: for (; ; ) {
    for (; null === a.sibling; ) {
      if (null === a.return || Tj(a.return)) return null;
      a = a.return;
    }
    a.sibling.return = a.return;
    for (a = a.sibling; 5 !== a.tag && 6 !== a.tag && 18 !== a.tag; ) {
      if (a.flags & 2) continue a;
      if (null === a.child || 4 === a.tag) continue a;
      else a.child.return = a, a = a.child;
    }
    if (!(a.flags & 2)) return a.stateNode;
  }
}
function Vj(a, b, c) {
  var d = a.tag;
  if (5 === d || 6 === d) a = a.stateNode, b ? 8 === c.nodeType ? c.parentNode.insertBefore(a, b) : c.insertBefore(a, b) : (8 === c.nodeType ? (b = c.parentNode, b.insertBefore(a, c)) : (b = c, b.appendChild(a)), c = c._reactRootContainer, null !== c && void 0 !== c || null !== b.onclick || (b.onclick = Bf));
  else if (4 !== d && (a = a.child, null !== a)) for (Vj(a, b, c), a = a.sibling; null !== a; ) Vj(a, b, c), a = a.sibling;
}
function Wj(a, b, c) {
  var d = a.tag;
  if (5 === d || 6 === d) a = a.stateNode, b ? c.insertBefore(a, b) : c.appendChild(a);
  else if (4 !== d && (a = a.child, null !== a)) for (Wj(a, b, c), a = a.sibling; null !== a; ) Wj(a, b, c), a = a.sibling;
}
var X = null, Xj = false;
function Yj(a, b, c) {
  for (c = c.child; null !== c; ) Zj(a, b, c), c = c.sibling;
}
function Zj(a, b, c) {
  if (lc && "function" === typeof lc.onCommitFiberUnmount) try {
    lc.onCommitFiberUnmount(kc, c);
  } catch (h) {
  }
  switch (c.tag) {
    case 5:
      U || Lj(c, b);
    case 6:
      var d = X, e = Xj;
      X = null;
      Yj(a, b, c);
      X = d;
      Xj = e;
      null !== X && (Xj ? (a = X, c = c.stateNode, 8 === a.nodeType ? a.parentNode.removeChild(c) : a.removeChild(c)) : X.removeChild(c.stateNode));
      break;
    case 18:
      null !== X && (Xj ? (a = X, c = c.stateNode, 8 === a.nodeType ? Kf(a.parentNode, c) : 1 === a.nodeType && Kf(a, c), bd(a)) : Kf(X, c.stateNode));
      break;
    case 4:
      d = X;
      e = Xj;
      X = c.stateNode.containerInfo;
      Xj = true;
      Yj(a, b, c);
      X = d;
      Xj = e;
      break;
    case 0:
    case 11:
    case 14:
    case 15:
      if (!U && (d = c.updateQueue, null !== d && (d = d.lastEffect, null !== d))) {
        e = d = d.next;
        do {
          var f2 = e, g = f2.destroy;
          f2 = f2.tag;
          void 0 !== g && (0 !== (f2 & 2) ? Mj(c, b, g) : 0 !== (f2 & 4) && Mj(c, b, g));
          e = e.next;
        } while (e !== d);
      }
      Yj(a, b, c);
      break;
    case 1:
      if (!U && (Lj(c, b), d = c.stateNode, "function" === typeof d.componentWillUnmount)) try {
        d.props = c.memoizedProps, d.state = c.memoizedState, d.componentWillUnmount();
      } catch (h) {
        W(c, b, h);
      }
      Yj(a, b, c);
      break;
    case 21:
      Yj(a, b, c);
      break;
    case 22:
      c.mode & 1 ? (U = (d = U) || null !== c.memoizedState, Yj(a, b, c), U = d) : Yj(a, b, c);
      break;
    default:
      Yj(a, b, c);
  }
}
function ak(a) {
  var b = a.updateQueue;
  if (null !== b) {
    a.updateQueue = null;
    var c = a.stateNode;
    null === c && (c = a.stateNode = new Kj());
    b.forEach(function(b2) {
      var d = bk.bind(null, a, b2);
      c.has(b2) || (c.add(b2), b2.then(d, d));
    });
  }
}
function ck(a, b) {
  var c = b.deletions;
  if (null !== c) for (var d = 0; d < c.length; d++) {
    var e = c[d];
    try {
      var f2 = a, g = b, h = g;
      a: for (; null !== h; ) {
        switch (h.tag) {
          case 5:
            X = h.stateNode;
            Xj = false;
            break a;
          case 3:
            X = h.stateNode.containerInfo;
            Xj = true;
            break a;
          case 4:
            X = h.stateNode.containerInfo;
            Xj = true;
            break a;
        }
        h = h.return;
      }
      if (null === X) throw Error(p(160));
      Zj(f2, g, e);
      X = null;
      Xj = false;
      var k2 = e.alternate;
      null !== k2 && (k2.return = null);
      e.return = null;
    } catch (l2) {
      W(e, b, l2);
    }
  }
  if (b.subtreeFlags & 12854) for (b = b.child; null !== b; ) dk(b, a), b = b.sibling;
}
function dk(a, b) {
  var c = a.alternate, d = a.flags;
  switch (a.tag) {
    case 0:
    case 11:
    case 14:
    case 15:
      ck(b, a);
      ek(a);
      if (d & 4) {
        try {
          Pj(3, a, a.return), Qj(3, a);
        } catch (t2) {
          W(a, a.return, t2);
        }
        try {
          Pj(5, a, a.return);
        } catch (t2) {
          W(a, a.return, t2);
        }
      }
      break;
    case 1:
      ck(b, a);
      ek(a);
      d & 512 && null !== c && Lj(c, c.return);
      break;
    case 5:
      ck(b, a);
      ek(a);
      d & 512 && null !== c && Lj(c, c.return);
      if (a.flags & 32) {
        var e = a.stateNode;
        try {
          ob(e, "");
        } catch (t2) {
          W(a, a.return, t2);
        }
      }
      if (d & 4 && (e = a.stateNode, null != e)) {
        var f2 = a.memoizedProps, g = null !== c ? c.memoizedProps : f2, h = a.type, k2 = a.updateQueue;
        a.updateQueue = null;
        if (null !== k2) try {
          "input" === h && "radio" === f2.type && null != f2.name && ab(e, f2);
          vb(h, g);
          var l2 = vb(h, f2);
          for (g = 0; g < k2.length; g += 2) {
            var m2 = k2[g], q2 = k2[g + 1];
            "style" === m2 ? sb(e, q2) : "dangerouslySetInnerHTML" === m2 ? nb(e, q2) : "children" === m2 ? ob(e, q2) : ta(e, m2, q2, l2);
          }
          switch (h) {
            case "input":
              bb(e, f2);
              break;
            case "textarea":
              ib(e, f2);
              break;
            case "select":
              var r2 = e._wrapperState.wasMultiple;
              e._wrapperState.wasMultiple = !!f2.multiple;
              var y2 = f2.value;
              null != y2 ? fb(e, !!f2.multiple, y2, false) : r2 !== !!f2.multiple && (null != f2.defaultValue ? fb(
                e,
                !!f2.multiple,
                f2.defaultValue,
                true
              ) : fb(e, !!f2.multiple, f2.multiple ? [] : "", false));
          }
          e[Pf] = f2;
        } catch (t2) {
          W(a, a.return, t2);
        }
      }
      break;
    case 6:
      ck(b, a);
      ek(a);
      if (d & 4) {
        if (null === a.stateNode) throw Error(p(162));
        e = a.stateNode;
        f2 = a.memoizedProps;
        try {
          e.nodeValue = f2;
        } catch (t2) {
          W(a, a.return, t2);
        }
      }
      break;
    case 3:
      ck(b, a);
      ek(a);
      if (d & 4 && null !== c && c.memoizedState.isDehydrated) try {
        bd(b.containerInfo);
      } catch (t2) {
        W(a, a.return, t2);
      }
      break;
    case 4:
      ck(b, a);
      ek(a);
      break;
    case 13:
      ck(b, a);
      ek(a);
      e = a.child;
      e.flags & 8192 && (f2 = null !== e.memoizedState, e.stateNode.isHidden = f2, !f2 || null !== e.alternate && null !== e.alternate.memoizedState || (fk = B()));
      d & 4 && ak(a);
      break;
    case 22:
      m2 = null !== c && null !== c.memoizedState;
      a.mode & 1 ? (U = (l2 = U) || m2, ck(b, a), U = l2) : ck(b, a);
      ek(a);
      if (d & 8192) {
        l2 = null !== a.memoizedState;
        if ((a.stateNode.isHidden = l2) && !m2 && 0 !== (a.mode & 1)) for (V = a, m2 = a.child; null !== m2; ) {
          for (q2 = V = m2; null !== V; ) {
            r2 = V;
            y2 = r2.child;
            switch (r2.tag) {
              case 0:
              case 11:
              case 14:
              case 15:
                Pj(4, r2, r2.return);
                break;
              case 1:
                Lj(r2, r2.return);
                var n2 = r2.stateNode;
                if ("function" === typeof n2.componentWillUnmount) {
                  d = r2;
                  c = r2.return;
                  try {
                    b = d, n2.props = b.memoizedProps, n2.state = b.memoizedState, n2.componentWillUnmount();
                  } catch (t2) {
                    W(d, c, t2);
                  }
                }
                break;
              case 5:
                Lj(r2, r2.return);
                break;
              case 22:
                if (null !== r2.memoizedState) {
                  gk(q2);
                  continue;
                }
            }
            null !== y2 ? (y2.return = r2, V = y2) : gk(q2);
          }
          m2 = m2.sibling;
        }
        a: for (m2 = null, q2 = a; ; ) {
          if (5 === q2.tag) {
            if (null === m2) {
              m2 = q2;
              try {
                e = q2.stateNode, l2 ? (f2 = e.style, "function" === typeof f2.setProperty ? f2.setProperty("display", "none", "important") : f2.display = "none") : (h = q2.stateNode, k2 = q2.memoizedProps.style, g = void 0 !== k2 && null !== k2 && k2.hasOwnProperty("display") ? k2.display : null, h.style.display = rb("display", g));
              } catch (t2) {
                W(a, a.return, t2);
              }
            }
          } else if (6 === q2.tag) {
            if (null === m2) try {
              q2.stateNode.nodeValue = l2 ? "" : q2.memoizedProps;
            } catch (t2) {
              W(a, a.return, t2);
            }
          } else if ((22 !== q2.tag && 23 !== q2.tag || null === q2.memoizedState || q2 === a) && null !== q2.child) {
            q2.child.return = q2;
            q2 = q2.child;
            continue;
          }
          if (q2 === a) break a;
          for (; null === q2.sibling; ) {
            if (null === q2.return || q2.return === a) break a;
            m2 === q2 && (m2 = null);
            q2 = q2.return;
          }
          m2 === q2 && (m2 = null);
          q2.sibling.return = q2.return;
          q2 = q2.sibling;
        }
      }
      break;
    case 19:
      ck(b, a);
      ek(a);
      d & 4 && ak(a);
      break;
    case 21:
      break;
    default:
      ck(
        b,
        a
      ), ek(a);
  }
}
function ek(a) {
  var b = a.flags;
  if (b & 2) {
    try {
      a: {
        for (var c = a.return; null !== c; ) {
          if (Tj(c)) {
            var d = c;
            break a;
          }
          c = c.return;
        }
        throw Error(p(160));
      }
      switch (d.tag) {
        case 5:
          var e = d.stateNode;
          d.flags & 32 && (ob(e, ""), d.flags &= -33);
          var f2 = Uj(a);
          Wj(a, f2, e);
          break;
        case 3:
        case 4:
          var g = d.stateNode.containerInfo, h = Uj(a);
          Vj(a, h, g);
          break;
        default:
          throw Error(p(161));
      }
    } catch (k2) {
      W(a, a.return, k2);
    }
    a.flags &= -3;
  }
  b & 4096 && (a.flags &= -4097);
}
function hk(a, b, c) {
  V = a;
  ik(a);
}
function ik(a, b, c) {
  for (var d = 0 !== (a.mode & 1); null !== V; ) {
    var e = V, f2 = e.child;
    if (22 === e.tag && d) {
      var g = null !== e.memoizedState || Jj;
      if (!g) {
        var h = e.alternate, k2 = null !== h && null !== h.memoizedState || U;
        h = Jj;
        var l2 = U;
        Jj = g;
        if ((U = k2) && !l2) for (V = e; null !== V; ) g = V, k2 = g.child, 22 === g.tag && null !== g.memoizedState ? jk(e) : null !== k2 ? (k2.return = g, V = k2) : jk(e);
        for (; null !== f2; ) V = f2, ik(f2), f2 = f2.sibling;
        V = e;
        Jj = h;
        U = l2;
      }
      kk(a);
    } else 0 !== (e.subtreeFlags & 8772) && null !== f2 ? (f2.return = e, V = f2) : kk(a);
  }
}
function kk(a) {
  for (; null !== V; ) {
    var b = V;
    if (0 !== (b.flags & 8772)) {
      var c = b.alternate;
      try {
        if (0 !== (b.flags & 8772)) switch (b.tag) {
          case 0:
          case 11:
          case 15:
            U || Qj(5, b);
            break;
          case 1:
            var d = b.stateNode;
            if (b.flags & 4 && !U) if (null === c) d.componentDidMount();
            else {
              var e = b.elementType === b.type ? c.memoizedProps : Ci(b.type, c.memoizedProps);
              d.componentDidUpdate(e, c.memoizedState, d.__reactInternalSnapshotBeforeUpdate);
            }
            var f2 = b.updateQueue;
            null !== f2 && sh(b, f2, d);
            break;
          case 3:
            var g = b.updateQueue;
            if (null !== g) {
              c = null;
              if (null !== b.child) switch (b.child.tag) {
                case 5:
                  c = b.child.stateNode;
                  break;
                case 1:
                  c = b.child.stateNode;
              }
              sh(b, g, c);
            }
            break;
          case 5:
            var h = b.stateNode;
            if (null === c && b.flags & 4) {
              c = h;
              var k2 = b.memoizedProps;
              switch (b.type) {
                case "button":
                case "input":
                case "select":
                case "textarea":
                  k2.autoFocus && c.focus();
                  break;
                case "img":
                  k2.src && (c.src = k2.src);
              }
            }
            break;
          case 6:
            break;
          case 4:
            break;
          case 12:
            break;
          case 13:
            if (null === b.memoizedState) {
              var l2 = b.alternate;
              if (null !== l2) {
                var m2 = l2.memoizedState;
                if (null !== m2) {
                  var q2 = m2.dehydrated;
                  null !== q2 && bd(q2);
                }
              }
            }
            break;
          case 19:
          case 17:
          case 21:
          case 22:
          case 23:
          case 25:
            break;
          default:
            throw Error(p(163));
        }
        U || b.flags & 512 && Rj(b);
      } catch (r2) {
        W(b, b.return, r2);
      }
    }
    if (b === a) {
      V = null;
      break;
    }
    c = b.sibling;
    if (null !== c) {
      c.return = b.return;
      V = c;
      break;
    }
    V = b.return;
  }
}
function gk(a) {
  for (; null !== V; ) {
    var b = V;
    if (b === a) {
      V = null;
      break;
    }
    var c = b.sibling;
    if (null !== c) {
      c.return = b.return;
      V = c;
      break;
    }
    V = b.return;
  }
}
function jk(a) {
  for (; null !== V; ) {
    var b = V;
    try {
      switch (b.tag) {
        case 0:
        case 11:
        case 15:
          var c = b.return;
          try {
            Qj(4, b);
          } catch (k2) {
            W(b, c, k2);
          }
          break;
        case 1:
          var d = b.stateNode;
          if ("function" === typeof d.componentDidMount) {
            var e = b.return;
            try {
              d.componentDidMount();
            } catch (k2) {
              W(b, e, k2);
            }
          }
          var f2 = b.return;
          try {
            Rj(b);
          } catch (k2) {
            W(b, f2, k2);
          }
          break;
        case 5:
          var g = b.return;
          try {
            Rj(b);
          } catch (k2) {
            W(b, g, k2);
          }
      }
    } catch (k2) {
      W(b, b.return, k2);
    }
    if (b === a) {
      V = null;
      break;
    }
    var h = b.sibling;
    if (null !== h) {
      h.return = b.return;
      V = h;
      break;
    }
    V = b.return;
  }
}
var lk = Math.ceil, mk = ua.ReactCurrentDispatcher, nk = ua.ReactCurrentOwner, ok = ua.ReactCurrentBatchConfig, K = 0, Q = null, Y = null, Z = 0, fj = 0, ej = Uf(0), T = 0, pk = null, rh = 0, qk = 0, rk = 0, sk = null, tk = null, fk = 0, Gj = Infinity, uk = null, Oi = false, Pi = null, Ri = null, vk = false, wk = null, xk = 0, yk = 0, zk = null, Ak = -1, Bk = 0;
function R() {
  return 0 !== (K & 6) ? B() : -1 !== Ak ? Ak : Ak = B();
}
function yi(a) {
  if (0 === (a.mode & 1)) return 1;
  if (0 !== (K & 2) && 0 !== Z) return Z & -Z;
  if (null !== Kg.transition) return 0 === Bk && (Bk = yc()), Bk;
  a = C;
  if (0 !== a) return a;
  a = window.event;
  a = void 0 === a ? 16 : jd(a.type);
  return a;
}
function gi(a, b, c, d) {
  if (50 < yk) throw yk = 0, zk = null, Error(p(185));
  Ac(a, c, d);
  if (0 === (K & 2) || a !== Q) a === Q && (0 === (K & 2) && (qk |= c), 4 === T && Ck(a, Z)), Dk(a, d), 1 === c && 0 === K && 0 === (b.mode & 1) && (Gj = B() + 500, fg && jg());
}
function Dk(a, b) {
  var c = a.callbackNode;
  wc(a, b);
  var d = uc(a, a === Q ? Z : 0);
  if (0 === d) null !== c && bc(c), a.callbackNode = null, a.callbackPriority = 0;
  else if (b = d & -d, a.callbackPriority !== b) {
    null != c && bc(c);
    if (1 === b) 0 === a.tag ? ig(Ek.bind(null, a)) : hg(Ek.bind(null, a)), Jf(function() {
      0 === (K & 6) && jg();
    }), c = null;
    else {
      switch (Dc(d)) {
        case 1:
          c = fc;
          break;
        case 4:
          c = gc;
          break;
        case 16:
          c = hc;
          break;
        case 536870912:
          c = jc;
          break;
        default:
          c = hc;
      }
      c = Fk(c, Gk.bind(null, a));
    }
    a.callbackPriority = b;
    a.callbackNode = c;
  }
}
function Gk(a, b) {
  Ak = -1;
  Bk = 0;
  if (0 !== (K & 6)) throw Error(p(327));
  var c = a.callbackNode;
  if (Hk() && a.callbackNode !== c) return null;
  var d = uc(a, a === Q ? Z : 0);
  if (0 === d) return null;
  if (0 !== (d & 30) || 0 !== (d & a.expiredLanes) || b) b = Ik(a, d);
  else {
    b = d;
    var e = K;
    K |= 2;
    var f2 = Jk();
    if (Q !== a || Z !== b) uk = null, Gj = B() + 500, Kk(a, b);
    do
      try {
        Lk();
        break;
      } catch (h) {
        Mk(a, h);
      }
    while (1);
    $g();
    mk.current = f2;
    K = e;
    null !== Y ? b = 0 : (Q = null, Z = 0, b = T);
  }
  if (0 !== b) {
    2 === b && (e = xc(a), 0 !== e && (d = e, b = Nk(a, e)));
    if (1 === b) throw c = pk, Kk(a, 0), Ck(a, d), Dk(a, B()), c;
    if (6 === b) Ck(a, d);
    else {
      e = a.current.alternate;
      if (0 === (d & 30) && !Ok(e) && (b = Ik(a, d), 2 === b && (f2 = xc(a), 0 !== f2 && (d = f2, b = Nk(a, f2))), 1 === b)) throw c = pk, Kk(a, 0), Ck(a, d), Dk(a, B()), c;
      a.finishedWork = e;
      a.finishedLanes = d;
      switch (b) {
        case 0:
        case 1:
          throw Error(p(345));
        case 2:
          Pk(a, tk, uk);
          break;
        case 3:
          Ck(a, d);
          if ((d & 130023424) === d && (b = fk + 500 - B(), 10 < b)) {
            if (0 !== uc(a, 0)) break;
            e = a.suspendedLanes;
            if ((e & d) !== d) {
              R();
              a.pingedLanes |= a.suspendedLanes & e;
              break;
            }
            a.timeoutHandle = Ff(Pk.bind(null, a, tk, uk), b);
            break;
          }
          Pk(a, tk, uk);
          break;
        case 4:
          Ck(a, d);
          if ((d & 4194240) === d) break;
          b = a.eventTimes;
          for (e = -1; 0 < d; ) {
            var g = 31 - oc(d);
            f2 = 1 << g;
            g = b[g];
            g > e && (e = g);
            d &= ~f2;
          }
          d = e;
          d = B() - d;
          d = (120 > d ? 120 : 480 > d ? 480 : 1080 > d ? 1080 : 1920 > d ? 1920 : 3e3 > d ? 3e3 : 4320 > d ? 4320 : 1960 * lk(d / 1960)) - d;
          if (10 < d) {
            a.timeoutHandle = Ff(Pk.bind(null, a, tk, uk), d);
            break;
          }
          Pk(a, tk, uk);
          break;
        case 5:
          Pk(a, tk, uk);
          break;
        default:
          throw Error(p(329));
      }
    }
  }
  Dk(a, B());
  return a.callbackNode === c ? Gk.bind(null, a) : null;
}
function Nk(a, b) {
  var c = sk;
  a.current.memoizedState.isDehydrated && (Kk(a, b).flags |= 256);
  a = Ik(a, b);
  2 !== a && (b = tk, tk = c, null !== b && Fj(b));
  return a;
}
function Fj(a) {
  null === tk ? tk = a : tk.push.apply(tk, a);
}
function Ok(a) {
  for (var b = a; ; ) {
    if (b.flags & 16384) {
      var c = b.updateQueue;
      if (null !== c && (c = c.stores, null !== c)) for (var d = 0; d < c.length; d++) {
        var e = c[d], f2 = e.getSnapshot;
        e = e.value;
        try {
          if (!He(f2(), e)) return false;
        } catch (g) {
          return false;
        }
      }
    }
    c = b.child;
    if (b.subtreeFlags & 16384 && null !== c) c.return = b, b = c;
    else {
      if (b === a) break;
      for (; null === b.sibling; ) {
        if (null === b.return || b.return === a) return true;
        b = b.return;
      }
      b.sibling.return = b.return;
      b = b.sibling;
    }
  }
  return true;
}
function Ck(a, b) {
  b &= ~rk;
  b &= ~qk;
  a.suspendedLanes |= b;
  a.pingedLanes &= ~b;
  for (a = a.expirationTimes; 0 < b; ) {
    var c = 31 - oc(b), d = 1 << c;
    a[c] = -1;
    b &= ~d;
  }
}
function Ek(a) {
  if (0 !== (K & 6)) throw Error(p(327));
  Hk();
  var b = uc(a, 0);
  if (0 === (b & 1)) return Dk(a, B()), null;
  var c = Ik(a, b);
  if (0 !== a.tag && 2 === c) {
    var d = xc(a);
    0 !== d && (b = d, c = Nk(a, d));
  }
  if (1 === c) throw c = pk, Kk(a, 0), Ck(a, b), Dk(a, B()), c;
  if (6 === c) throw Error(p(345));
  a.finishedWork = a.current.alternate;
  a.finishedLanes = b;
  Pk(a, tk, uk);
  Dk(a, B());
  return null;
}
function Qk(a, b) {
  var c = K;
  K |= 1;
  try {
    return a(b);
  } finally {
    K = c, 0 === K && (Gj = B() + 500, fg && jg());
  }
}
function Rk(a) {
  null !== wk && 0 === wk.tag && 0 === (K & 6) && Hk();
  var b = K;
  K |= 1;
  var c = ok.transition, d = C;
  try {
    if (ok.transition = null, C = 1, a) return a();
  } finally {
    C = d, ok.transition = c, K = b, 0 === (K & 6) && jg();
  }
}
function Hj() {
  fj = ej.current;
  E(ej);
}
function Kk(a, b) {
  a.finishedWork = null;
  a.finishedLanes = 0;
  var c = a.timeoutHandle;
  -1 !== c && (a.timeoutHandle = -1, Gf(c));
  if (null !== Y) for (c = Y.return; null !== c; ) {
    var d = c;
    wg(d);
    switch (d.tag) {
      case 1:
        d = d.type.childContextTypes;
        null !== d && void 0 !== d && $f();
        break;
      case 3:
        zh();
        E(Wf);
        E(H);
        Eh();
        break;
      case 5:
        Bh(d);
        break;
      case 4:
        zh();
        break;
      case 13:
        E(L);
        break;
      case 19:
        E(L);
        break;
      case 10:
        ah(d.type._context);
        break;
      case 22:
      case 23:
        Hj();
    }
    c = c.return;
  }
  Q = a;
  Y = a = Pg(a.current, null);
  Z = fj = b;
  T = 0;
  pk = null;
  rk = qk = rh = 0;
  tk = sk = null;
  if (null !== fh) {
    for (b = 0; b < fh.length; b++) if (c = fh[b], d = c.interleaved, null !== d) {
      c.interleaved = null;
      var e = d.next, f2 = c.pending;
      if (null !== f2) {
        var g = f2.next;
        f2.next = e;
        d.next = g;
      }
      c.pending = d;
    }
    fh = null;
  }
  return a;
}
function Mk(a, b) {
  do {
    var c = Y;
    try {
      $g();
      Fh.current = Rh;
      if (Ih) {
        for (var d = M.memoizedState; null !== d; ) {
          var e = d.queue;
          null !== e && (e.pending = null);
          d = d.next;
        }
        Ih = false;
      }
      Hh = 0;
      O = N = M = null;
      Jh = false;
      Kh = 0;
      nk.current = null;
      if (null === c || null === c.return) {
        T = 1;
        pk = b;
        Y = null;
        break;
      }
      a: {
        var f2 = a, g = c.return, h = c, k2 = b;
        b = Z;
        h.flags |= 32768;
        if (null !== k2 && "object" === typeof k2 && "function" === typeof k2.then) {
          var l2 = k2, m2 = h, q2 = m2.tag;
          if (0 === (m2.mode & 1) && (0 === q2 || 11 === q2 || 15 === q2)) {
            var r2 = m2.alternate;
            r2 ? (m2.updateQueue = r2.updateQueue, m2.memoizedState = r2.memoizedState, m2.lanes = r2.lanes) : (m2.updateQueue = null, m2.memoizedState = null);
          }
          var y2 = Ui(g);
          if (null !== y2) {
            y2.flags &= -257;
            Vi(y2, g, h, f2, b);
            y2.mode & 1 && Si(f2, l2, b);
            b = y2;
            k2 = l2;
            var n2 = b.updateQueue;
            if (null === n2) {
              var t2 = /* @__PURE__ */ new Set();
              t2.add(k2);
              b.updateQueue = t2;
            } else n2.add(k2);
            break a;
          } else {
            if (0 === (b & 1)) {
              Si(f2, l2, b);
              tj();
              break a;
            }
            k2 = Error(p(426));
          }
        } else if (I && h.mode & 1) {
          var J2 = Ui(g);
          if (null !== J2) {
            0 === (J2.flags & 65536) && (J2.flags |= 256);
            Vi(J2, g, h, f2, b);
            Jg(Ji(k2, h));
            break a;
          }
        }
        f2 = k2 = Ji(k2, h);
        4 !== T && (T = 2);
        null === sk ? sk = [f2] : sk.push(f2);
        f2 = g;
        do {
          switch (f2.tag) {
            case 3:
              f2.flags |= 65536;
              b &= -b;
              f2.lanes |= b;
              var x2 = Ni(f2, k2, b);
              ph(f2, x2);
              break a;
            case 1:
              h = k2;
              var w2 = f2.type, u2 = f2.stateNode;
              if (0 === (f2.flags & 128) && ("function" === typeof w2.getDerivedStateFromError || null !== u2 && "function" === typeof u2.componentDidCatch && (null === Ri || !Ri.has(u2)))) {
                f2.flags |= 65536;
                b &= -b;
                f2.lanes |= b;
                var F2 = Qi(f2, h, b);
                ph(f2, F2);
                break a;
              }
          }
          f2 = f2.return;
        } while (null !== f2);
      }
      Sk(c);
    } catch (na) {
      b = na;
      Y === c && null !== c && (Y = c = c.return);
      continue;
    }
    break;
  } while (1);
}
function Jk() {
  var a = mk.current;
  mk.current = Rh;
  return null === a ? Rh : a;
}
function tj() {
  if (0 === T || 3 === T || 2 === T) T = 4;
  null === Q || 0 === (rh & 268435455) && 0 === (qk & 268435455) || Ck(Q, Z);
}
function Ik(a, b) {
  var c = K;
  K |= 2;
  var d = Jk();
  if (Q !== a || Z !== b) uk = null, Kk(a, b);
  do
    try {
      Tk();
      break;
    } catch (e) {
      Mk(a, e);
    }
  while (1);
  $g();
  K = c;
  mk.current = d;
  if (null !== Y) throw Error(p(261));
  Q = null;
  Z = 0;
  return T;
}
function Tk() {
  for (; null !== Y; ) Uk(Y);
}
function Lk() {
  for (; null !== Y && !cc(); ) Uk(Y);
}
function Uk(a) {
  var b = Vk(a.alternate, a, fj);
  a.memoizedProps = a.pendingProps;
  null === b ? Sk(a) : Y = b;
  nk.current = null;
}
function Sk(a) {
  var b = a;
  do {
    var c = b.alternate;
    a = b.return;
    if (0 === (b.flags & 32768)) {
      if (c = Ej(c, b, fj), null !== c) {
        Y = c;
        return;
      }
    } else {
      c = Ij(c, b);
      if (null !== c) {
        c.flags &= 32767;
        Y = c;
        return;
      }
      if (null !== a) a.flags |= 32768, a.subtreeFlags = 0, a.deletions = null;
      else {
        T = 6;
        Y = null;
        return;
      }
    }
    b = b.sibling;
    if (null !== b) {
      Y = b;
      return;
    }
    Y = b = a;
  } while (null !== b);
  0 === T && (T = 5);
}
function Pk(a, b, c) {
  var d = C, e = ok.transition;
  try {
    ok.transition = null, C = 1, Wk(a, b, c, d);
  } finally {
    ok.transition = e, C = d;
  }
  return null;
}
function Wk(a, b, c, d) {
  do
    Hk();
  while (null !== wk);
  if (0 !== (K & 6)) throw Error(p(327));
  c = a.finishedWork;
  var e = a.finishedLanes;
  if (null === c) return null;
  a.finishedWork = null;
  a.finishedLanes = 0;
  if (c === a.current) throw Error(p(177));
  a.callbackNode = null;
  a.callbackPriority = 0;
  var f2 = c.lanes | c.childLanes;
  Bc(a, f2);
  a === Q && (Y = Q = null, Z = 0);
  0 === (c.subtreeFlags & 2064) && 0 === (c.flags & 2064) || vk || (vk = true, Fk(hc, function() {
    Hk();
    return null;
  }));
  f2 = 0 !== (c.flags & 15990);
  if (0 !== (c.subtreeFlags & 15990) || f2) {
    f2 = ok.transition;
    ok.transition = null;
    var g = C;
    C = 1;
    var h = K;
    K |= 4;
    nk.current = null;
    Oj(a, c);
    dk(c, a);
    Oe(Df);
    dd = !!Cf;
    Df = Cf = null;
    a.current = c;
    hk(c);
    dc();
    K = h;
    C = g;
    ok.transition = f2;
  } else a.current = c;
  vk && (vk = false, wk = a, xk = e);
  f2 = a.pendingLanes;
  0 === f2 && (Ri = null);
  mc(c.stateNode);
  Dk(a, B());
  if (null !== b) for (d = a.onRecoverableError, c = 0; c < b.length; c++) e = b[c], d(e.value, { componentStack: e.stack, digest: e.digest });
  if (Oi) throw Oi = false, a = Pi, Pi = null, a;
  0 !== (xk & 1) && 0 !== a.tag && Hk();
  f2 = a.pendingLanes;
  0 !== (f2 & 1) ? a === zk ? yk++ : (yk = 0, zk = a) : yk = 0;
  jg();
  return null;
}
function Hk() {
  if (null !== wk) {
    var a = Dc(xk), b = ok.transition, c = C;
    try {
      ok.transition = null;
      C = 16 > a ? 16 : a;
      if (null === wk) var d = false;
      else {
        a = wk;
        wk = null;
        xk = 0;
        if (0 !== (K & 6)) throw Error(p(331));
        var e = K;
        K |= 4;
        for (V = a.current; null !== V; ) {
          var f2 = V, g = f2.child;
          if (0 !== (V.flags & 16)) {
            var h = f2.deletions;
            if (null !== h) {
              for (var k2 = 0; k2 < h.length; k2++) {
                var l2 = h[k2];
                for (V = l2; null !== V; ) {
                  var m2 = V;
                  switch (m2.tag) {
                    case 0:
                    case 11:
                    case 15:
                      Pj(8, m2, f2);
                  }
                  var q2 = m2.child;
                  if (null !== q2) q2.return = m2, V = q2;
                  else for (; null !== V; ) {
                    m2 = V;
                    var r2 = m2.sibling, y2 = m2.return;
                    Sj(m2);
                    if (m2 === l2) {
                      V = null;
                      break;
                    }
                    if (null !== r2) {
                      r2.return = y2;
                      V = r2;
                      break;
                    }
                    V = y2;
                  }
                }
              }
              var n2 = f2.alternate;
              if (null !== n2) {
                var t2 = n2.child;
                if (null !== t2) {
                  n2.child = null;
                  do {
                    var J2 = t2.sibling;
                    t2.sibling = null;
                    t2 = J2;
                  } while (null !== t2);
                }
              }
              V = f2;
            }
          }
          if (0 !== (f2.subtreeFlags & 2064) && null !== g) g.return = f2, V = g;
          else b: for (; null !== V; ) {
            f2 = V;
            if (0 !== (f2.flags & 2048)) switch (f2.tag) {
              case 0:
              case 11:
              case 15:
                Pj(9, f2, f2.return);
            }
            var x2 = f2.sibling;
            if (null !== x2) {
              x2.return = f2.return;
              V = x2;
              break b;
            }
            V = f2.return;
          }
        }
        var w2 = a.current;
        for (V = w2; null !== V; ) {
          g = V;
          var u2 = g.child;
          if (0 !== (g.subtreeFlags & 2064) && null !== u2) u2.return = g, V = u2;
          else b: for (g = w2; null !== V; ) {
            h = V;
            if (0 !== (h.flags & 2048)) try {
              switch (h.tag) {
                case 0:
                case 11:
                case 15:
                  Qj(9, h);
              }
            } catch (na) {
              W(h, h.return, na);
            }
            if (h === g) {
              V = null;
              break b;
            }
            var F2 = h.sibling;
            if (null !== F2) {
              F2.return = h.return;
              V = F2;
              break b;
            }
            V = h.return;
          }
        }
        K = e;
        jg();
        if (lc && "function" === typeof lc.onPostCommitFiberRoot) try {
          lc.onPostCommitFiberRoot(kc, a);
        } catch (na) {
        }
        d = true;
      }
      return d;
    } finally {
      C = c, ok.transition = b;
    }
  }
  return false;
}
function Xk(a, b, c) {
  b = Ji(c, b);
  b = Ni(a, b, 1);
  a = nh(a, b, 1);
  b = R();
  null !== a && (Ac(a, 1, b), Dk(a, b));
}
function W(a, b, c) {
  if (3 === a.tag) Xk(a, a, c);
  else for (; null !== b; ) {
    if (3 === b.tag) {
      Xk(b, a, c);
      break;
    } else if (1 === b.tag) {
      var d = b.stateNode;
      if ("function" === typeof b.type.getDerivedStateFromError || "function" === typeof d.componentDidCatch && (null === Ri || !Ri.has(d))) {
        a = Ji(c, a);
        a = Qi(b, a, 1);
        b = nh(b, a, 1);
        a = R();
        null !== b && (Ac(b, 1, a), Dk(b, a));
        break;
      }
    }
    b = b.return;
  }
}
function Ti(a, b, c) {
  var d = a.pingCache;
  null !== d && d.delete(b);
  b = R();
  a.pingedLanes |= a.suspendedLanes & c;
  Q === a && (Z & c) === c && (4 === T || 3 === T && (Z & 130023424) === Z && 500 > B() - fk ? Kk(a, 0) : rk |= c);
  Dk(a, b);
}
function Yk(a, b) {
  0 === b && (0 === (a.mode & 1) ? b = 1 : (b = sc, sc <<= 1, 0 === (sc & 130023424) && (sc = 4194304)));
  var c = R();
  a = ih(a, b);
  null !== a && (Ac(a, b, c), Dk(a, c));
}
function uj(a) {
  var b = a.memoizedState, c = 0;
  null !== b && (c = b.retryLane);
  Yk(a, c);
}
function bk(a, b) {
  var c = 0;
  switch (a.tag) {
    case 13:
      var d = a.stateNode;
      var e = a.memoizedState;
      null !== e && (c = e.retryLane);
      break;
    case 19:
      d = a.stateNode;
      break;
    default:
      throw Error(p(314));
  }
  null !== d && d.delete(b);
  Yk(a, c);
}
var Vk;
Vk = function(a, b, c) {
  if (null !== a) if (a.memoizedProps !== b.pendingProps || Wf.current) dh = true;
  else {
    if (0 === (a.lanes & c) && 0 === (b.flags & 128)) return dh = false, yj(a, b, c);
    dh = 0 !== (a.flags & 131072) ? true : false;
  }
  else dh = false, I && 0 !== (b.flags & 1048576) && ug(b, ng, b.index);
  b.lanes = 0;
  switch (b.tag) {
    case 2:
      var d = b.type;
      ij(a, b);
      a = b.pendingProps;
      var e = Yf(b, H.current);
      ch(b, c);
      e = Nh(null, b, d, a, e, c);
      var f2 = Sh();
      b.flags |= 1;
      "object" === typeof e && null !== e && "function" === typeof e.render && void 0 === e.$$typeof ? (b.tag = 1, b.memoizedState = null, b.updateQueue = null, Zf(d) ? (f2 = true, cg(b)) : f2 = false, b.memoizedState = null !== e.state && void 0 !== e.state ? e.state : null, kh(b), e.updater = Ei, b.stateNode = e, e._reactInternals = b, Ii(b, d, a, c), b = jj(null, b, d, true, f2, c)) : (b.tag = 0, I && f2 && vg(b), Xi(null, b, e, c), b = b.child);
      return b;
    case 16:
      d = b.elementType;
      a: {
        ij(a, b);
        a = b.pendingProps;
        e = d._init;
        d = e(d._payload);
        b.type = d;
        e = b.tag = Zk(d);
        a = Ci(d, a);
        switch (e) {
          case 0:
            b = cj(null, b, d, a, c);
            break a;
          case 1:
            b = hj(null, b, d, a, c);
            break a;
          case 11:
            b = Yi(null, b, d, a, c);
            break a;
          case 14:
            b = $i(null, b, d, Ci(d.type, a), c);
            break a;
        }
        throw Error(p(
          306,
          d,
          ""
        ));
      }
      return b;
    case 0:
      return d = b.type, e = b.pendingProps, e = b.elementType === d ? e : Ci(d, e), cj(a, b, d, e, c);
    case 1:
      return d = b.type, e = b.pendingProps, e = b.elementType === d ? e : Ci(d, e), hj(a, b, d, e, c);
    case 3:
      a: {
        kj(b);
        if (null === a) throw Error(p(387));
        d = b.pendingProps;
        f2 = b.memoizedState;
        e = f2.element;
        lh(a, b);
        qh(b, d, null, c);
        var g = b.memoizedState;
        d = g.element;
        if (f2.isDehydrated) if (f2 = { element: d, isDehydrated: false, cache: g.cache, pendingSuspenseBoundaries: g.pendingSuspenseBoundaries, transitions: g.transitions }, b.updateQueue.baseState = f2, b.memoizedState = f2, b.flags & 256) {
          e = Ji(Error(p(423)), b);
          b = lj(a, b, d, c, e);
          break a;
        } else if (d !== e) {
          e = Ji(Error(p(424)), b);
          b = lj(a, b, d, c, e);
          break a;
        } else for (yg = Lf(b.stateNode.containerInfo.firstChild), xg = b, I = true, zg = null, c = Vg(b, null, d, c), b.child = c; c; ) c.flags = c.flags & -3 | 4096, c = c.sibling;
        else {
          Ig();
          if (d === e) {
            b = Zi(a, b, c);
            break a;
          }
          Xi(a, b, d, c);
        }
        b = b.child;
      }
      return b;
    case 5:
      return Ah(b), null === a && Eg(b), d = b.type, e = b.pendingProps, f2 = null !== a ? a.memoizedProps : null, g = e.children, Ef(d, e) ? g = null : null !== f2 && Ef(d, f2) && (b.flags |= 32), gj(a, b), Xi(a, b, g, c), b.child;
    case 6:
      return null === a && Eg(b), null;
    case 13:
      return oj(a, b, c);
    case 4:
      return yh(b, b.stateNode.containerInfo), d = b.pendingProps, null === a ? b.child = Ug(b, null, d, c) : Xi(a, b, d, c), b.child;
    case 11:
      return d = b.type, e = b.pendingProps, e = b.elementType === d ? e : Ci(d, e), Yi(a, b, d, e, c);
    case 7:
      return Xi(a, b, b.pendingProps, c), b.child;
    case 8:
      return Xi(a, b, b.pendingProps.children, c), b.child;
    case 12:
      return Xi(a, b, b.pendingProps.children, c), b.child;
    case 10:
      a: {
        d = b.type._context;
        e = b.pendingProps;
        f2 = b.memoizedProps;
        g = e.value;
        G(Wg, d._currentValue);
        d._currentValue = g;
        if (null !== f2) if (He(f2.value, g)) {
          if (f2.children === e.children && !Wf.current) {
            b = Zi(a, b, c);
            break a;
          }
        } else for (f2 = b.child, null !== f2 && (f2.return = b); null !== f2; ) {
          var h = f2.dependencies;
          if (null !== h) {
            g = f2.child;
            for (var k2 = h.firstContext; null !== k2; ) {
              if (k2.context === d) {
                if (1 === f2.tag) {
                  k2 = mh(-1, c & -c);
                  k2.tag = 2;
                  var l2 = f2.updateQueue;
                  if (null !== l2) {
                    l2 = l2.shared;
                    var m2 = l2.pending;
                    null === m2 ? k2.next = k2 : (k2.next = m2.next, m2.next = k2);
                    l2.pending = k2;
                  }
                }
                f2.lanes |= c;
                k2 = f2.alternate;
                null !== k2 && (k2.lanes |= c);
                bh(
                  f2.return,
                  c,
                  b
                );
                h.lanes |= c;
                break;
              }
              k2 = k2.next;
            }
          } else if (10 === f2.tag) g = f2.type === b.type ? null : f2.child;
          else if (18 === f2.tag) {
            g = f2.return;
            if (null === g) throw Error(p(341));
            g.lanes |= c;
            h = g.alternate;
            null !== h && (h.lanes |= c);
            bh(g, c, b);
            g = f2.sibling;
          } else g = f2.child;
          if (null !== g) g.return = f2;
          else for (g = f2; null !== g; ) {
            if (g === b) {
              g = null;
              break;
            }
            f2 = g.sibling;
            if (null !== f2) {
              f2.return = g.return;
              g = f2;
              break;
            }
            g = g.return;
          }
          f2 = g;
        }
        Xi(a, b, e.children, c);
        b = b.child;
      }
      return b;
    case 9:
      return e = b.type, d = b.pendingProps.children, ch(b, c), e = eh(e), d = d(e), b.flags |= 1, Xi(a, b, d, c), b.child;
    case 14:
      return d = b.type, e = Ci(d, b.pendingProps), e = Ci(d.type, e), $i(a, b, d, e, c);
    case 15:
      return bj(a, b, b.type, b.pendingProps, c);
    case 17:
      return d = b.type, e = b.pendingProps, e = b.elementType === d ? e : Ci(d, e), ij(a, b), b.tag = 1, Zf(d) ? (a = true, cg(b)) : a = false, ch(b, c), Gi(b, d, e), Ii(b, d, e, c), jj(null, b, d, true, a, c);
    case 19:
      return xj(a, b, c);
    case 22:
      return dj(a, b, c);
  }
  throw Error(p(156, b.tag));
};
function Fk(a, b) {
  return ac(a, b);
}
function $k(a, b, c, d) {
  this.tag = a;
  this.key = c;
  this.sibling = this.child = this.return = this.stateNode = this.type = this.elementType = null;
  this.index = 0;
  this.ref = null;
  this.pendingProps = b;
  this.dependencies = this.memoizedState = this.updateQueue = this.memoizedProps = null;
  this.mode = d;
  this.subtreeFlags = this.flags = 0;
  this.deletions = null;
  this.childLanes = this.lanes = 0;
  this.alternate = null;
}
function Bg(a, b, c, d) {
  return new $k(a, b, c, d);
}
function aj(a) {
  a = a.prototype;
  return !(!a || !a.isReactComponent);
}
function Zk(a) {
  if ("function" === typeof a) return aj(a) ? 1 : 0;
  if (void 0 !== a && null !== a) {
    a = a.$$typeof;
    if (a === Da) return 11;
    if (a === Ga) return 14;
  }
  return 2;
}
function Pg(a, b) {
  var c = a.alternate;
  null === c ? (c = Bg(a.tag, b, a.key, a.mode), c.elementType = a.elementType, c.type = a.type, c.stateNode = a.stateNode, c.alternate = a, a.alternate = c) : (c.pendingProps = b, c.type = a.type, c.flags = 0, c.subtreeFlags = 0, c.deletions = null);
  c.flags = a.flags & 14680064;
  c.childLanes = a.childLanes;
  c.lanes = a.lanes;
  c.child = a.child;
  c.memoizedProps = a.memoizedProps;
  c.memoizedState = a.memoizedState;
  c.updateQueue = a.updateQueue;
  b = a.dependencies;
  c.dependencies = null === b ? null : { lanes: b.lanes, firstContext: b.firstContext };
  c.sibling = a.sibling;
  c.index = a.index;
  c.ref = a.ref;
  return c;
}
function Rg(a, b, c, d, e, f2) {
  var g = 2;
  d = a;
  if ("function" === typeof a) aj(a) && (g = 1);
  else if ("string" === typeof a) g = 5;
  else a: switch (a) {
    case ya:
      return Tg(c.children, e, f2, b);
    case za:
      g = 8;
      e |= 8;
      break;
    case Aa:
      return a = Bg(12, c, b, e | 2), a.elementType = Aa, a.lanes = f2, a;
    case Ea:
      return a = Bg(13, c, b, e), a.elementType = Ea, a.lanes = f2, a;
    case Fa:
      return a = Bg(19, c, b, e), a.elementType = Fa, a.lanes = f2, a;
    case Ia:
      return pj(c, e, f2, b);
    default:
      if ("object" === typeof a && null !== a) switch (a.$$typeof) {
        case Ba:
          g = 10;
          break a;
        case Ca:
          g = 9;
          break a;
        case Da:
          g = 11;
          break a;
        case Ga:
          g = 14;
          break a;
        case Ha:
          g = 16;
          d = null;
          break a;
      }
      throw Error(p(130, null == a ? a : typeof a, ""));
  }
  b = Bg(g, c, b, e);
  b.elementType = a;
  b.type = d;
  b.lanes = f2;
  return b;
}
function Tg(a, b, c, d) {
  a = Bg(7, a, d, b);
  a.lanes = c;
  return a;
}
function pj(a, b, c, d) {
  a = Bg(22, a, d, b);
  a.elementType = Ia;
  a.lanes = c;
  a.stateNode = { isHidden: false };
  return a;
}
function Qg(a, b, c) {
  a = Bg(6, a, null, b);
  a.lanes = c;
  return a;
}
function Sg(a, b, c) {
  b = Bg(4, null !== a.children ? a.children : [], a.key, b);
  b.lanes = c;
  b.stateNode = { containerInfo: a.containerInfo, pendingChildren: null, implementation: a.implementation };
  return b;
}
function al(a, b, c, d, e) {
  this.tag = b;
  this.containerInfo = a;
  this.finishedWork = this.pingCache = this.current = this.pendingChildren = null;
  this.timeoutHandle = -1;
  this.callbackNode = this.pendingContext = this.context = null;
  this.callbackPriority = 0;
  this.eventTimes = zc(0);
  this.expirationTimes = zc(-1);
  this.entangledLanes = this.finishedLanes = this.mutableReadLanes = this.expiredLanes = this.pingedLanes = this.suspendedLanes = this.pendingLanes = 0;
  this.entanglements = zc(0);
  this.identifierPrefix = d;
  this.onRecoverableError = e;
  this.mutableSourceEagerHydrationData = null;
}
function bl(a, b, c, d, e, f2, g, h, k2) {
  a = new al(a, b, c, h, k2);
  1 === b ? (b = 1, true === f2 && (b |= 8)) : b = 0;
  f2 = Bg(3, null, null, b);
  a.current = f2;
  f2.stateNode = a;
  f2.memoizedState = { element: d, isDehydrated: c, cache: null, transitions: null, pendingSuspenseBoundaries: null };
  kh(f2);
  return a;
}
function cl(a, b, c) {
  var d = 3 < arguments.length && void 0 !== arguments[3] ? arguments[3] : null;
  return { $$typeof: wa, key: null == d ? null : "" + d, children: a, containerInfo: b, implementation: c };
}
function dl(a) {
  if (!a) return Vf;
  a = a._reactInternals;
  a: {
    if (Vb(a) !== a || 1 !== a.tag) throw Error(p(170));
    var b = a;
    do {
      switch (b.tag) {
        case 3:
          b = b.stateNode.context;
          break a;
        case 1:
          if (Zf(b.type)) {
            b = b.stateNode.__reactInternalMemoizedMergedChildContext;
            break a;
          }
      }
      b = b.return;
    } while (null !== b);
    throw Error(p(171));
  }
  if (1 === a.tag) {
    var c = a.type;
    if (Zf(c)) return bg(a, c, b);
  }
  return b;
}
function el(a, b, c, d, e, f2, g, h, k2) {
  a = bl(c, d, true, a, e, f2, g, h, k2);
  a.context = dl(null);
  c = a.current;
  d = R();
  e = yi(c);
  f2 = mh(d, e);
  f2.callback = void 0 !== b && null !== b ? b : null;
  nh(c, f2, e);
  a.current.lanes = e;
  Ac(a, e, d);
  Dk(a, d);
  return a;
}
function fl(a, b, c, d) {
  var e = b.current, f2 = R(), g = yi(e);
  c = dl(c);
  null === b.context ? b.context = c : b.pendingContext = c;
  b = mh(f2, g);
  b.payload = { element: a };
  d = void 0 === d ? null : d;
  null !== d && (b.callback = d);
  a = nh(e, b, g);
  null !== a && (gi(a, e, g, f2), oh(a, e, g));
  return g;
}
function gl(a) {
  a = a.current;
  if (!a.child) return null;
  switch (a.child.tag) {
    case 5:
      return a.child.stateNode;
    default:
      return a.child.stateNode;
  }
}
function hl(a, b) {
  a = a.memoizedState;
  if (null !== a && null !== a.dehydrated) {
    var c = a.retryLane;
    a.retryLane = 0 !== c && c < b ? c : b;
  }
}
function il(a, b) {
  hl(a, b);
  (a = a.alternate) && hl(a, b);
}
function jl() {
  return null;
}
var kl = "function" === typeof reportError ? reportError : function(a) {
  console.error(a);
};
function ll(a) {
  this._internalRoot = a;
}
ml.prototype.render = ll.prototype.render = function(a) {
  var b = this._internalRoot;
  if (null === b) throw Error(p(409));
  fl(a, b, null, null);
};
ml.prototype.unmount = ll.prototype.unmount = function() {
  var a = this._internalRoot;
  if (null !== a) {
    this._internalRoot = null;
    var b = a.containerInfo;
    Rk(function() {
      fl(null, a, null, null);
    });
    b[uf] = null;
  }
};
function ml(a) {
  this._internalRoot = a;
}
ml.prototype.unstable_scheduleHydration = function(a) {
  if (a) {
    var b = Hc();
    a = { blockedOn: null, target: a, priority: b };
    for (var c = 0; c < Qc.length && 0 !== b && b < Qc[c].priority; c++) ;
    Qc.splice(c, 0, a);
    0 === c && Vc(a);
  }
};
function nl(a) {
  return !(!a || 1 !== a.nodeType && 9 !== a.nodeType && 11 !== a.nodeType);
}
function ol(a) {
  return !(!a || 1 !== a.nodeType && 9 !== a.nodeType && 11 !== a.nodeType && (8 !== a.nodeType || " react-mount-point-unstable " !== a.nodeValue));
}
function pl() {
}
function ql(a, b, c, d, e) {
  if (e) {
    if ("function" === typeof d) {
      var f2 = d;
      d = function() {
        var a2 = gl(g);
        f2.call(a2);
      };
    }
    var g = el(b, d, a, 0, null, false, false, "", pl);
    a._reactRootContainer = g;
    a[uf] = g.current;
    sf(8 === a.nodeType ? a.parentNode : a);
    Rk();
    return g;
  }
  for (; e = a.lastChild; ) a.removeChild(e);
  if ("function" === typeof d) {
    var h = d;
    d = function() {
      var a2 = gl(k2);
      h.call(a2);
    };
  }
  var k2 = bl(a, 0, false, null, null, false, false, "", pl);
  a._reactRootContainer = k2;
  a[uf] = k2.current;
  sf(8 === a.nodeType ? a.parentNode : a);
  Rk(function() {
    fl(b, k2, c, d);
  });
  return k2;
}
function rl(a, b, c, d, e) {
  var f2 = c._reactRootContainer;
  if (f2) {
    var g = f2;
    if ("function" === typeof e) {
      var h = e;
      e = function() {
        var a2 = gl(g);
        h.call(a2);
      };
    }
    fl(b, g, a, e);
  } else g = ql(c, b, a, e, d);
  return gl(g);
}
Ec = function(a) {
  switch (a.tag) {
    case 3:
      var b = a.stateNode;
      if (b.current.memoizedState.isDehydrated) {
        var c = tc(b.pendingLanes);
        0 !== c && (Cc(b, c | 1), Dk(b, B()), 0 === (K & 6) && (Gj = B() + 500, jg()));
      }
      break;
    case 13:
      Rk(function() {
        var b2 = ih(a, 1);
        if (null !== b2) {
          var c2 = R();
          gi(b2, a, 1, c2);
        }
      }), il(a, 1);
  }
};
Fc = function(a) {
  if (13 === a.tag) {
    var b = ih(a, 134217728);
    if (null !== b) {
      var c = R();
      gi(b, a, 134217728, c);
    }
    il(a, 134217728);
  }
};
Gc = function(a) {
  if (13 === a.tag) {
    var b = yi(a), c = ih(a, b);
    if (null !== c) {
      var d = R();
      gi(c, a, b, d);
    }
    il(a, b);
  }
};
Hc = function() {
  return C;
};
Ic = function(a, b) {
  var c = C;
  try {
    return C = a, b();
  } finally {
    C = c;
  }
};
yb = function(a, b, c) {
  switch (b) {
    case "input":
      bb(a, c);
      b = c.name;
      if ("radio" === c.type && null != b) {
        for (c = a; c.parentNode; ) c = c.parentNode;
        c = c.querySelectorAll("input[name=" + JSON.stringify("" + b) + '][type="radio"]');
        for (b = 0; b < c.length; b++) {
          var d = c[b];
          if (d !== a && d.form === a.form) {
            var e = Db(d);
            if (!e) throw Error(p(90));
            Wa(d);
            bb(d, e);
          }
        }
      }
      break;
    case "textarea":
      ib(a, c);
      break;
    case "select":
      b = c.value, null != b && fb(a, !!c.multiple, b, false);
  }
};
Gb = Qk;
Hb = Rk;
var sl = { usingClientEntryPoint: false, Events: [Cb, ue, Db, Eb, Fb, Qk] }, tl = { findFiberByHostInstance: Wc, bundleType: 0, version: "18.3.1", rendererPackageName: "react-dom" };
var ul = { bundleType: tl.bundleType, version: tl.version, rendererPackageName: tl.rendererPackageName, rendererConfig: tl.rendererConfig, overrideHookState: null, overrideHookStateDeletePath: null, overrideHookStateRenamePath: null, overrideProps: null, overridePropsDeletePath: null, overridePropsRenamePath: null, setErrorHandler: null, setSuspenseHandler: null, scheduleUpdate: null, currentDispatcherRef: ua.ReactCurrentDispatcher, findHostInstanceByFiber: function(a) {
  a = Zb(a);
  return null === a ? null : a.stateNode;
}, findFiberByHostInstance: tl.findFiberByHostInstance || jl, findHostInstancesForRefresh: null, scheduleRefresh: null, scheduleRoot: null, setRefreshHandler: null, getCurrentFiber: null, reconcilerVersion: "18.3.1-next-f1338f8080-20240426" };
if ("undefined" !== typeof __REACT_DEVTOOLS_GLOBAL_HOOK__) {
  var vl = __REACT_DEVTOOLS_GLOBAL_HOOK__;
  if (!vl.isDisabled && vl.supportsFiber) try {
    kc = vl.inject(ul), lc = vl;
  } catch (a) {
  }
}
reactDom_production_min.__SECRET_INTERNALS_DO_NOT_USE_OR_YOU_WILL_BE_FIRED = sl;
reactDom_production_min.createPortal = function(a, b) {
  var c = 2 < arguments.length && void 0 !== arguments[2] ? arguments[2] : null;
  if (!nl(b)) throw Error(p(200));
  return cl(a, b, null, c);
};
reactDom_production_min.createRoot = function(a, b) {
  if (!nl(a)) throw Error(p(299));
  var c = false, d = "", e = kl;
  null !== b && void 0 !== b && (true === b.unstable_strictMode && (c = true), void 0 !== b.identifierPrefix && (d = b.identifierPrefix), void 0 !== b.onRecoverableError && (e = b.onRecoverableError));
  b = bl(a, 1, false, null, null, c, false, d, e);
  a[uf] = b.current;
  sf(8 === a.nodeType ? a.parentNode : a);
  return new ll(b);
};
reactDom_production_min.findDOMNode = function(a) {
  if (null == a) return null;
  if (1 === a.nodeType) return a;
  var b = a._reactInternals;
  if (void 0 === b) {
    if ("function" === typeof a.render) throw Error(p(188));
    a = Object.keys(a).join(",");
    throw Error(p(268, a));
  }
  a = Zb(b);
  a = null === a ? null : a.stateNode;
  return a;
};
reactDom_production_min.flushSync = function(a) {
  return Rk(a);
};
reactDom_production_min.hydrate = function(a, b, c) {
  if (!ol(b)) throw Error(p(200));
  return rl(null, a, b, true, c);
};
reactDom_production_min.hydrateRoot = function(a, b, c) {
  if (!nl(a)) throw Error(p(405));
  var d = null != c && c.hydratedSources || null, e = false, f2 = "", g = kl;
  null !== c && void 0 !== c && (true === c.unstable_strictMode && (e = true), void 0 !== c.identifierPrefix && (f2 = c.identifierPrefix), void 0 !== c.onRecoverableError && (g = c.onRecoverableError));
  b = el(b, null, a, 1, null != c ? c : null, e, false, f2, g);
  a[uf] = b.current;
  sf(a);
  if (d) for (a = 0; a < d.length; a++) c = d[a], e = c._getVersion, e = e(c._source), null == b.mutableSourceEagerHydrationData ? b.mutableSourceEagerHydrationData = [c, e] : b.mutableSourceEagerHydrationData.push(
    c,
    e
  );
  return new ml(b);
};
reactDom_production_min.render = function(a, b, c) {
  if (!ol(b)) throw Error(p(200));
  return rl(null, a, b, false, c);
};
reactDom_production_min.unmountComponentAtNode = function(a) {
  if (!ol(a)) throw Error(p(40));
  return a._reactRootContainer ? (Rk(function() {
    rl(null, null, a, false, function() {
      a._reactRootContainer = null;
      a[uf] = null;
    });
  }), true) : false;
};
reactDom_production_min.unstable_batchedUpdates = Qk;
reactDom_production_min.unstable_renderSubtreeIntoContainer = function(a, b, c, d) {
  if (!ol(c)) throw Error(p(200));
  if (null == a || void 0 === a._reactInternals) throw Error(p(38));
  return rl(a, b, c, false, d);
};
reactDom_production_min.version = "18.3.1-next-f1338f8080-20240426";
function checkDCE() {
  if (typeof __REACT_DEVTOOLS_GLOBAL_HOOK__ === "undefined" || typeof __REACT_DEVTOOLS_GLOBAL_HOOK__.checkDCE !== "function") {
    return;
  }
  try {
    __REACT_DEVTOOLS_GLOBAL_HOOK__.checkDCE(checkDCE);
  } catch (err) {
    console.error(err);
  }
}
{
  checkDCE();
  reactDom.exports = reactDom_production_min;
}
var reactDomExports = reactDom.exports;
var m = reactDomExports;
{
  client.createRoot = m.createRoot;
  client.hydrateRoot = m.hydrateRoot;
}
function nativeApi() {
  const api = globalThis.native;
  return api !== void 0 && typeof api.skinIcon === "function" ? api : null;
}
const MOCK_NOW = Date.now();
const ago = (h) => new Date(MOCK_NOW - h * 36e5).toISOString();
const MOCK_VERSIONS = [
  { id: "neoforge-26.2.0.88", sizeBytes: 26843545, lastPlayedAt: ago(5), isDefault: true },
  { id: "26.3-rc-3", sizeBytes: 3969e4, lastPlayedAt: ago(30) },
  { id: "26.3-4", sizeBytes: 3969e4, lastPlayedAt: ago(48) },
  { id: "fabric-loader-0.16.9-26.3", sizeBytes: 4121e4, lastPlayedAt: ago(72) },
  { id: "1.21.11", sizeBytes: 3804e4, lastPlayedAt: ago(120) },
  { id: "1.20.4", sizeBytes: 24445539, lastPlayedAt: ago(24) }
];
const MOCK_ACCOUNTS = [
  { uuid: "a1", name: "Notch", type: "microsoft", isDefault: true, hasSession: true },
  { uuid: "c3", name: "Steve", type: "offline", isDefault: false, hasSession: true },
  {
    uuid: "e5",
    name: "皮肤站玩家",
    type: "external",
    isDefault: false,
    hasSession: false,
    serverName: "LittleSkin"
  }
];
const MOCK_SKINS = [
  {
    id: "sk0a1b2c3d4e5",
    name: "经典 Steve",
    model: "classic",
    fileName: "sk0a1b2c3d4e5.png",
    path: "/home/user/.starcloud/skins/sk0a1b2c3d4e5.png",
    source: "file",
    width: 64,
    height: 64,
    addedAt: ago(52)
  },
  {
    id: "sk1b2c3d4e5f6",
    name: "纤细 Alex",
    model: "slim",
    fileName: "sk1b2c3d4e5f6.png",
    path: "/home/user/.starcloud/skins/sk1b2c3d4e5f6.png",
    source: "url",
    sourceUrl: "https://littleskin.cn/skin/alex.png",
    width: 64,
    height: 64,
    addedAt: ago(9)
  },
  {
    id: "sk2c3d4e5f6a7",
    name: "高清 128×128",
    model: "auto",
    fileName: "sk2c3d4e5f6a7.png",
    path: "/home/user/.starcloud/skins/sk2c3d4e5f6a7.png",
    source: "file",
    width: 128,
    height: 128,
    addedAt: ago(200)
  }
];
const CONTENT_FILE_MOCK = [
  {
    fileName: "sodium-1.20.4.jar",
    displayName: "sodium-1.20.4.jar",
    enabled: true,
    sizeBytes: 124e4,
    path: "/mods/sodium.jar"
  },
  {
    fileName: "jei-1.20.4.jar.disabled",
    displayName: "jei-1.20.4.jar",
    enabled: false,
    sizeBytes: 89e4,
    path: "/mods/jei.jar.disabled"
  },
  {
    fileName: "rubidium-1.20.4.jar",
    displayName: "rubidium-1.20.4.jar",
    enabled: true,
    sizeBytes: 21e5,
    path: "/mods/rubidium.jar"
  }
];
const LAB_BLOCK_IDS = [
  "minecraft:stone",
  "minecraft:cobblestone",
  "minecraft:oak_planks",
  "minecraft:glass",
  "minecraft:oak_stairs",
  "minecraft:torch",
  "minecraft:chest"
];
function mockOf(method, p2) {
  switch (method) {
    case "catalog.installed":
      return { installed: MOCK_VERSIONS };
    case "catalog.versions":
      return {
        remote: Array.from({ length: 24 }, (_, i) => ({
          id: `1.${21 - Math.floor(i / 8)}.${i % 8}`,
          type: i % 5 === 0 ? "snapshot" : "release",
          url: "",
          releaseTime: new Date(MOCK_NOW - i * 6048e5).toISOString(),
          installed: i < 2
        })),
        installed: MOCK_VERSIONS.map(({ id: id2, sizeBytes }) => ({ id: id2, sizeBytes }))
      };
    case "accounts.list":
      return { accounts: MOCK_ACCOUNTS, current: "a1" };
    case "accounts.addOffline":
    case "accounts.setCurrent":
    case "accounts.remove":
    case "settings.apply":
    case "version.saveSettings":
      return {};
    case "launch.start":
      return { sessionId: 1, pid: 4242, logFile: "/tmp/game.log" };
    case "settings.get":
      return {
        gameDir: "D:\\Games\\.minecraft",
        javaExecutable: "",
        maxMemoryMb: 6144,
        downloadSource: "auto"
      };
    case "java.list":
      return { runtimes: [{ versionString: "17.0.20.1", majorVersion: 17 }] };
    case "saves.list":
      return {
        saves: ["生存 · 主世界", "建筑 · 城市", "红石 · 农场", "探索 · 群岛"].map(
          (levelName, i) => ({
            folderName: `world-${i + 1}`,
            levelName,
            gameRoot: "D:\\Games\\.minecraft",
            versionId: i % 2 === 0 ? "1.20.4" : "",
            isolated: false,
            sizeBytes: 42e6 + i * 7e6,
            lastPlayedAt: ago(i * 48)
          })
        )
      };
    case "saves.openFolder":
      return { ok: true, path: "D:\\Games\\.minecraft\\saves" };
    case "saves.backup":
      return { ok: true, path: "backup.zip", sizeBytes: 12e6 };
    case "saves.delete":
      return { ok: true, freedBytes: 42e6 };
    case "version.detail": {
      const id2 = String(p2.versionId ?? "1.20.4");
      return {
        versionId: id2,
        gameRoot: `D:\\Games\\.minecraft\\versions\\${id2}`,
        isolated: true,
        mcVersion: "1.20.4",
        loader: "neoforge",
        lastPlayedAt: ago(24),
        mods: CONTENT_FILE_MOCK,
        resourcepacks: [
          {
            fileName: "Faithful-64x.zip",
            displayName: "Faithful-64x.zip",
            enabled: true,
            sizeBytes: 39e5,
            path: "/rp/faithful.zip"
          }
        ],
        shaderpacks: [
          {
            fileName: "complementary.zip",
            displayName: "BSL 8.zip",
            enabled: true,
            sizeBytes: 73e5,
            path: "/shaders/bsl.zip"
          }
        ]
      };
    }
    case "version.settings":
      return {
        versionId: String(p2.versionId ?? "1.20.4"),
        isolation: "isolated",
        memoryMode: "manual",
        memoryMinMb: 1024,
        memoryMaxMb: 6144,
        javaMode: "auto",
        windowMode: "global",
        extraJvmArgs: [],
        extraGameArgs: [],
        effective: {
          gameRoot: "D:\\Games\\.minecraft\\versions\\1.20.4",
          isolated: true,
          minMem: 1024,
          maxMem: 6144,
          windowW: 854,
          windowH: 480,
          fullscreen: false
        }
      };
    case "version.toggleContent":
    case "version.deleteContent":
      return { files: CONTENT_FILE_MOCK };
    case "version.openContentFolder":
      return { ok: true, path: "D:\\Games\\.minecraft\\versions\\1.20.4\\mods" };
    case "version.checkUpdates":
      return {
        updates: [
          {
            fileName: "rubidium-1.20.4.jar",
            currentVersion: "1.0.0",
            latestVersion: "1.1.2",
            latestFileName: "rubidium-1.20.4.jar",
            latestUrl: ""
          }
        ]
      };
    case "version.updateMod":
      return {
        ok: true,
        fileName: String(p2.fileName),
        oldFileName: String(p2.fileName),
        latestVersion: "1.1.2"
      };
    case "version.checkDependencies":
      return {
        missing: [],
        optional: [{ projectId: "jei", title: "JEI" }],
        incompatible: [],
        truncated: false
      };
    case "versions.delete":
      return { deleted: true, freedBytes: 3969e4, wasDefault: false };
    case "versions.openFolder":
      return { ok: true, path: "D:\\Games\\.minecraft\\versions" };
    case "versions.setDefault":
      return { ok: true, lastVersion: String(p2.versionId) };
    case "content.search":
      return {
        totalHits: 128,
        hits: ["Sodium", "Iris Shaders", "JourneyMap", "Fabric API", "Lithium", "Phi"].map(
          (title, i) => ({
            projectId: `p${i}`,
            slug: `mod-${i}`,
            title,
            description: "显著降低内存占用、改善卡顿的优化类模组，适合长期生存档。",
            author: [
              "jellysquid3",
              "IMS",
              "TeamPlex",
              "FabricMC",
              "CaffeineMC",
              "FoundationGames"
            ][i],
            downloads: 248033070 - i * 31e5,
            iconUrl: "",
            displayCategories: ["optimization"],
            loaders: [],
            webUrl: ""
          })
        )
      };
    case "content.install":
      return {
        installed: [
          {
            projectId: String(p2.projectId),
            title: String(p2.title ?? ""),
            fileName: "mod.jar",
            path: "/mods/mod.jar"
          }
        ],
        missing: []
      };
    case "multiplayer.state":
      return {
        providers: [
          {
            id: "terracotta",
            displayName: "陶瓦联机",
            vendor: "Terracotta",
            vendorUrl: "",
            description: "基于 EasyTier 的 P2P 联机，无需公网 IP 或端口映射",
            builtIn: true
          }
        ],
        activeId: "terracotta",
        nickname: "Steve",
        nodeRegion: "auto",
        core: { installed: true, version: "1.4.0", sessionRunning: false },
        state: { phase: "idle", isBusy: false }
      };
    case "multiplayer.setNickname":
    case "multiplayer.setNodeRegion":
    case "multiplayer.selectProvider":
    case "multiplayer.host":
    case "multiplayer.join":
    case "multiplayer.idle":
    case "multiplayer.stop":
      return mockOf("multiplayer.state", {});
    case "multiplayer.logs":
      return { logs: "[terracotta] 核心已启动\n[terracotta] 等待房间指令…" };
    case "plugins.list":
      return {
        plugins: [
          {
            id: "dev.starcloud.demo",
            name: "示例插件",
            version: "1.2.0",
            apiVersion: "2.0",
            author: "Starcloud",
            description: "演示面板与动作调用",
            permissions: ["launcher:ui_panel", "launcher:storage"],
            runtime: "js",
            official: true,
            enabled: true,
            disclaimerAccepted: true,
            loaded: true,
            panelCount: 1,
            dir: "/plugins/demo",
            dependencies: {}
          },
          {
            id: "third.party",
            name: "第三方示例",
            version: "0.4.1",
            apiVersion: "2.0",
            author: "someone",
            description: "未验证的第三方插件",
            permissions: ["launcher:content_read"],
            runtime: "py",
            official: false,
            enabled: false,
            disclaimerAccepted: false,
            loaded: false,
            panelCount: 0,
            dir: "/plugins/third",
            dependencies: {}
          }
        ],
        pageTipShown: false
      };
    case "plugins.install":
      return {
        id: "x",
        name: String(p2.path ?? ""),
        version: "1.0.0",
        apiVersion: "2.0",
        author: "",
        description: "",
        permissions: [],
        official: false,
        enabled: false,
        disclaimerAccepted: false,
        loaded: false,
        panelCount: 0,
        dir: "",
        dependencies: {}
      };
    case "plugins.setEnabled":
    case "plugins.uninstall":
    case "plugins.openFolder":
      return { ok: true };
    case "lab.load":
      return {
        path: String(p2.path ?? "demo.litematic"),
        sizeX: 24,
        sizeY: 12,
        sizeZ: 32,
        truncated: false,
        totalBlocks: 2841,
        distinctBlocks: LAB_BLOCK_IDS.length,
        stats: LAB_BLOCK_IDS.map((id2, i) => ({
          id: id2,
          count: [1204, 640, 512, 241, 122, 78, 44][i]
        })),
        preview: Array.from({ length: 800 }, (_, i) => ({
          x: i % 24,
          y: Math.floor(i / 24) % 12,
          z: Math.floor(i / (24 * 12)),
          id: LAB_BLOCK_IDS[i % LAB_BLOCK_IDS.length]
        })),
        previewTruncated: true
      };
    case "lab.worldEater":
      return { ok: true, path: "world-eater.litematic" };
    case "nova.config.get":
      return { baseUrl: "", apiKey: "", model: "" };
    case "nova.config.set":
      return {
        baseUrl: String(p2.baseUrl ?? ""),
        apiKey: String(p2.apiKey ?? ""),
        model: String(p2.model ?? "")
      };
    case "nova.chat":
      return { accepted: true };
    case "auth.microsoft.start":
      return {
        userCode: "STAR-3F7K",
        deviceCode: "mock-device-code",
        verificationUri: "https://www.microsoft.com/link",
        interval: 5
      };
    case "auth.microsoft.poll":
      return { state: "pending", message: "等待用户授权" };
    case "auth.yggdrasil.login":
      return {
        state: "success",
        account: {
          uuid: "e5",
          name: String(p2.username ?? "Player"),
          type: "external",
          serverName: "Mock 皮肤站"
        }
      };
    case "skins.list":
      return { skins: MOCK_SKINS };
    case "skins.importPath":
    case "skins.importUrl": {
      const picked = MOCK_SKINS[0];
      return {
        skin: {
          ...picked,
          id: "sknew" + String(p2.url ?? p2.path ?? "").length,
          name: String(p2.name ?? "新导入的皮肤"),
          source: p2.url !== void 0 ? "url" : "file",
          sourceUrl: p2.url !== void 0 ? String(p2.url) : void 0,
          addedAt: new Date(MOCK_NOW).toISOString()
        }
      };
    }
    case "skins.rename":
      return {
        skin: {
          ...MOCK_SKINS.find((s) => s.id === p2.id) ?? MOCK_SKINS[0],
          name: String(p2.name)
        }
      };
    case "skins.setModel":
      return {
        skin: {
          ...MOCK_SKINS.find((s) => s.id === p2.id) ?? MOCK_SKINS[0],
          model: p2.model === "slim" ? "slim" : "classic"
        }
      };
    case "skins.delete":
      return { ok: true };
    case "skins.station.list":
      return { skins: [] };
    case "skins.station.apply":
      return { message: "皮肤已提交到皮肤站" };
    case "accounts.setSkin":
      return {
        account: {
          ...MOCK_ACCOUNTS.find((a) => a.uuid === p2.uuid) ?? MOCK_ACCOUNTS[0],
          skinId: p2.skinId === void 0 ? void 0 : String(p2.skinId),
          skinModel: p2.model === void 0 ? void 0 : String(p2.model)
        }
      };
    default:
      return {};
  }
}
const isMockMode = typeof window !== "undefined" && window.bridge === void 0;
function installMockBridgeIfNeeded() {
  if (!isMockMode || typeof window === "undefined") return;
  const handlers = [];
  const emit = (event, params) => {
    for (const handler of handlers) handler(event, params);
  };
  window.bridge = {
    request: async (method, params = {}) => {
      await new Promise((r2) => setTimeout(r2, 60));
      if (method === "launch.start") {
        setTimeout(() => emit("game.output", { line: "[INFO] Environment: Linux" }), 300);
        setTimeout(() => emit("game.output", { line: "[INFO] Setting user: Notch" }), 900);
        setTimeout(
          () => emit("game.output", { line: "[INFO] Backend library: LWJGL 3.3.2" }),
          1500
        );
      }
      return mockOf(method, params);
    },
    appVersion: async () => "0.3.4-mock"
  };
  window.bridgeEvents = { subscribe: (handler) => void handlers.push(handler) };
  console.info("[bridge] 浏览器调试模式：使用内置 mock 数据");
}
function cleanError(e) {
  const raw = e instanceof Error ? e.message : String(e);
  const stripped = raw.replace(/^Error invoking remote method '[^']*':\s*/i, "").replace(/^Error:\s*/i, "");
  return new Error(stripped === "" ? raw : stripped);
}
function request(method, params = {}) {
  if (typeof window === "undefined" || window.bridge === void 0) {
    return Promise.reject(new Error("桥接不可用"));
  }
  return window.bridge.request(method, params).catch((e) => {
    throw cleanError(e);
  });
}
function onBridgeEvent(handler) {
  if (typeof window === "undefined" || window.bridgeEvents === void 0) return;
  window.bridgeEvents.subscribe(handler);
}
function appVersion() {
  if (typeof window === "undefined" || window.bridge === void 0) {
    return Promise.resolve("0.3.4");
  }
  return window.bridge.appVersion();
}
const bridge = /* @__PURE__ */ Object.freeze(/* @__PURE__ */ Object.defineProperty({
  __proto__: null,
  appVersion,
  installMockBridgeIfNeeded,
  isMockMode,
  nativeApi,
  onBridgeEvent,
  request
}, Symbol.toStringTag, { value: "Module" }));
const labIcon = "data:image/svg+xml,%3csvg%20xmlns='http://www.w3.org/2000/svg'%20viewBox='0%200%2024%2024'%20width='24'%20height='24'%20role='img'%20aria-label='SC%20实验室'%3e%3c!--%20「SC%20实验室」导航图标：锥形瓶%20+%20液面%20+%20气泡（工具与创造）。%20浅底用（深色）描边：与同主题下的导航文字/图标同色，避免固定灰在浅底上发虚。%20--%3e%3cg%20transform='translate(12%2012)%20scale(1.15)%20translate(-12%20-12)'%20fill='none'%20stroke='%23111418'%20stroke-width='1.3'%20stroke-linecap='round'%20stroke-linejoin='round'%3e%3cpath%20d='M9,3.5%20L15,3.5%20M10,3.5%20L10,9.2%20L5.6,17.4%20A2.4,2.4%200%200%200%207.8,20.5%20L16.2,20.5%20A2.4,2.4%200%200%200%2018.4,17.4%20L14,9.2%20L14,3.5'/%3e%3cpath%20d='M6.9,15.4%20L17.1,15.4'/%3e%3ccircle%20cx='10.4'%20cy='18'%20r='0.9'/%3e%3ccircle%20cx='13.8'%20cy='18.6'%20r='0.7'/%3e%3c/g%3e%3c/svg%3e";
const labIconDark = "data:image/svg+xml,%3csvg%20xmlns='http://www.w3.org/2000/svg'%20viewBox='0%200%2024%2024'%20width='24'%20height='24'%20role='img'%20aria-label='SC%20实验室'%3e%3c!--%20「SC%20实验室」导航图标：锥形瓶%20+%20液面%20+%20气泡（工具与创造）。%20深底用（浅色）描边：与同主题下的导航文字/图标同色，避免固定灰在浅底上发虚。%20--%3e%3cg%20transform='translate(12%2012)%20scale(1.15)%20translate(-12%20-12)'%20fill='none'%20stroke='%23E6EAF2'%20stroke-width='1.3'%20stroke-linecap='round'%20stroke-linejoin='round'%3e%3cpath%20d='M9,3.5%20L15,3.5%20M10,3.5%20L10,9.2%20L5.6,17.4%20A2.4,2.4%200%200%200%207.8,20.5%20L16.2,20.5%20A2.4,2.4%200%200%200%2018.4,17.4%20L14,9.2%20L14,3.5'/%3e%3cpath%20d='M6.9,15.4%20L17.1,15.4'/%3e%3ccircle%20cx='10.4'%20cy='18'%20r='0.9'/%3e%3ccircle%20cx='13.8'%20cy='18.6'%20r='0.7'/%3e%3c/g%3e%3c/svg%3e";
const projectionIcon = "data:image/svg+xml,%3csvg%20xmlns='http://www.w3.org/2000/svg'%20viewBox='0%200%2024%2024'%20width='24'%20height='24'%20role='img'%20aria-label='投影管理'%3e%3c!--%20「投影管理」导航图标：等轴立方体悬浮在虚线投影台上（像被投影出来的模型）。%20浅底用（深色）描边：与同主题下的导航文字/图标同色。%20--%3e%3cg%20transform='translate(12%2012)%20scale(1.15)%20translate(-12%20-12)'%20fill='none'%20stroke='%23111418'%20stroke-width='1.3'%20stroke-linecap='round'%20stroke-linejoin='round'%3e%3cpath%20d='M12,2.8%20L18.8,6.7%20L18.8,14.1%20L12,18%20L5.2,14.1%20L5.2,6.7%20Z'/%3e%3cpath%20d='M5.2,6.7%20L12,10.6%20L18.8,6.7'/%3e%3cpath%20d='M12,10.6%20L12,18'/%3e%3cpath%20d='M6.4,20.5%20L17.6,20.5'%20stroke-dasharray='1.7%202'/%3e%3c/g%3e%3c/svg%3e";
const projectionIconDark = "data:image/svg+xml,%3csvg%20xmlns='http://www.w3.org/2000/svg'%20viewBox='0%200%2024%2024'%20width='24'%20height='24'%20role='img'%20aria-label='投影管理'%3e%3c!--%20「投影管理」导航图标：等轴立方体悬浮在虚线投影台上（像被投影出来的模型）。%20深底用（浅色）描边：与同主题下的导航文字/图标同色。%20--%3e%3cg%20transform='translate(12%2012)%20scale(1.15)%20translate(-12%20-12)'%20fill='none'%20stroke='%23E6EAF2'%20stroke-width='1.3'%20stroke-linecap='round'%20stroke-linejoin='round'%3e%3cpath%20d='M12,2.8%20L18.8,6.7%20L18.8,14.1%20L12,18%20L5.2,14.1%20L5.2,6.7%20Z'/%3e%3cpath%20d='M5.2,6.7%20L12,10.6%20L18.8,6.7'/%3e%3cpath%20d='M12,10.6%20L12,18'/%3e%3cpath%20d='M6.4,20.5%20L17.6,20.5'%20stroke-dasharray='1.7%202'/%3e%3c/g%3e%3c/svg%3e";
const brandMark = "data:image/svg+xml,%3csvg%20xmlns='http://www.w3.org/2000/svg'%20viewBox='491%20313%20220%20220'%20width='220'%20height='220'%20role='img'%20aria-label='Starcloud%20标记%20·%20小尺寸'%3e%3ctitle%3eStarcloud%20标记%20·%20小尺寸单块版（16–24px%20专用）%3c/title%3e%3c!--%20Starcloud%20/%20星云创新%20·%20小尺寸专用版%2016–24px%20下只留'变数'本身：抬起的黄块与星，去掉三个空心方格。%20理由：该尺寸下%209px%20描边只剩%200.14px，三个方格必然糊成脏点。%20倾斜%2012°%20保留%20——%20它是标记的性格，不随尺寸妥协。%2032px%20起请换回主标记%20starcloud-mark-light.svg%20/%20-dark.svg。%20--%3e%3cg%20transform='translate(601,423)%20rotate(12)%20scale(1.08)%20translate(-601,-423)'%3e%3crect%20x='526'%20y='348'%20width='150'%20height='150'%20rx='34'%20fill='%23FFD322'/%3e%3cpath%20fill='%23111418'%20d='M601,380%20L614.28,408.72%20L645.70,412.48%20L622.24,433.98%20L628.63,465.02%20L601,449.6%20L573.37,465.02%20L579.51,433.98%20L556.30,412.48%20L587.72,408.72%20Z'/%3e%3c/g%3e%3c/svg%3e";
const NAV = [
  { key: "home", label: "首页", group: "game", order: 0, icon: { kind: "glyph", code: String.fromCharCode(60535) } },
  { key: "versions", label: "版本", group: "game", order: 1, icon: { kind: "glyph", code: String.fromCharCode(60052) } },
  { key: "download", label: "下载", group: "game", order: 2, icon: { kind: "glyph", code: String.fromCharCode(59660) } },
  { key: "saves", label: "存档", group: "game", order: 3, icon: { kind: "glyph", code: String.fromCharCode(60048) } },
  { key: "multiplayer", label: "联机", group: "extra", order: 0, icon: { kind: "glyph", code: String.fromCharCode(60305) } },
  { key: "lab", label: "实验室", group: "extra", order: 1, icon: { kind: "svg", light: labIcon, dark: labIconDark } },
  { key: "projections", label: "投影管理", group: "extra", order: 2, icon: { kind: "svg", light: projectionIcon, dark: projectionIconDark } },
  { key: "running", label: "运行状态", group: "extra", order: 3, icon: { kind: "glyph", code: String.fromCharCode(60475) } },
  { key: "accounts", label: "账户", group: "extra", order: 4, icon: { kind: "glyph", code: String.fromCharCode(60303) } },
  { key: "plugins", label: "插件", group: "extra", order: 5, icon: { kind: "glyph", code: String.fromCharCode(60092) } },
  { key: "nova", label: "Nova", group: "extra", order: 6, icon: { kind: "brand", src: brandMark, size: 16 } },
  { key: "logs", label: "启动器日志", group: "footer", order: 0, icon: { kind: "glyph", code: String.fromCharCode(60516) } },
  { key: "settings", label: "设置", group: "footer", order: 1, icon: { kind: "glyph", code: String.fromCharCode(60420) } }
];
const PAGE_LABEL = NAV.reduce(
  (acc, n2) => ({ ...acc, [n2.key]: n2.label }),
  {}
);
function Nav(props) {
  const [open, setOpen] = reactExports.useState(false);
  const icon = (spec) => {
    if (spec.kind === "brand") {
      return /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "nav-icon nav-nova", children: /* @__PURE__ */ jsxRuntimeExports.jsx("img", { src: spec.src, alt: "", width: spec.size ?? 16, height: spec.size ?? 16 }) });
    }
    if (spec.kind === "svg") {
      return /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "nav-icon", children: /* @__PURE__ */ jsxRuntimeExports.jsx("img", { src: props.dark ? spec.dark : spec.light, alt: "", width: 18, height: 18 }) });
    }
    return /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "nav-icon", children: spec.code });
  };
  const item = (entry) => {
    const active = props.page === entry.key;
    return /* @__PURE__ */ jsxRuntimeExports.jsxs(
      "button",
      {
        className: `nav-item${active ? " nav-active" : ""}`,
        "data-key": entry.key,
        "data-label": entry.label,
        title: entry.label,
        onClick: () => props.onNavigate(entry.key),
        children: [
          icon(entry.icon),
          /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "nav-label", children: entry.label }),
          entry.key === "running" && props.running && /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "nav-run-dot" })
        ]
      },
      entry.key
    );
  };
  const game = NAV.filter((n2) => n2.group === "game").sort((a, b) => a.order - b.order);
  const extra = NAV.filter((n2) => n2.group === "extra").sort((a, b) => a.order - b.order);
  const footer = NAV.filter((n2) => n2.group === "footer").sort((a, b) => a.order - b.order);
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("nav", { className: `nav acrylic${open ? " nav-open" : ""}`, "data-theme-aware": true, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsxs(
      "button",
      {
        className: "nav-mode",
        title: open ? "收起导航" : "展开导航",
        "aria-label": open ? "收起导航" : "展开导航",
        onClick: () => setOpen((v2) => !v2),
        children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "nav-icon", style: { fontFamily: "var(--font-icon)" }, children: open ? String.fromCharCode(60038) : String.fromCharCode(60553) }),
          open && /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "nav-label", children: "收起" })
        ]
      }
    ),
    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "nav-group", children: game.map(item) }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "nav-sep" }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "nav-group", children: extra.map(item) }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "nav-spacer" }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "nav-group", children: footer.map(item) })
  ] });
}
const STORAGE_KEY = "starcloud.theme";
function themeApi() {
  return globalThis.theme;
}
function systemPrefersDark() {
  return window.matchMedia?.("(prefers-color-scheme: dark)").matches ?? true;
}
function readStoredMode() {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (raw === "light" || raw === "dark" || raw === "system") return raw;
  } catch {
  }
  return "system";
}
function applyTheme(resolved) {
  const root = document.documentElement;
  if (root.getAttribute("data-theme") !== resolved) {
    root.setAttribute("data-theme", resolved);
  }
  root.style.colorScheme = resolved;
}
function notifyWindowBackground(resolved) {
  themeApi()?.setWindowBackground?.(resolved === "dark");
}
async function resolveInitialTheme() {
  const mode = readStoredMode();
  let dark;
  if (mode === "system") {
    const api = themeApi();
    dark = api !== void 0 ? await api.get().then((s) => s.dark).catch(() => systemPrefersDark()) : systemPrefersDark();
  } else {
    dark = mode === "dark";
  }
  const resolved = dark ? "dark" : "light";
  applyTheme(resolved);
  notifyWindowBackground(resolved);
  return resolved;
}
function useTheme(initial) {
  const [systemDark, setSystemDark] = reactExports.useState(initial === "dark");
  const [mode, setModeState] = reactExports.useState(readStoredMode);
  const resolved = mode === "system" ? systemDark ? "dark" : "light" : mode;
  reactExports.useEffect(() => {
    const api = themeApi();
    if (api !== void 0) {
      api.onChange((s) => setSystemDark(s.dark));
      return;
    }
    const mq = window.matchMedia("(prefers-color-scheme: dark)");
    const onChange = (e) => setSystemDark(e.matches);
    mq.addEventListener("change", onChange);
    return () => mq.removeEventListener("change", onChange);
  }, []);
  const setMode = reactExports.useCallback((m2) => {
    setModeState(m2);
    try {
      localStorage.setItem(STORAGE_KEY, m2);
    } catch {
    }
  }, []);
  const cycle = reactExports.useCallback(() => {
    setMode(mode === "system" ? "light" : mode === "light" ? "dark" : "system");
  }, [mode, setMode]);
  reactExports.useEffect(() => {
    applyTheme(resolved);
    notifyWindowBackground(resolved);
  }, [resolved]);
  return { mode, resolved, setMode, cycle };
}
function applyVisualSettings(s) {
  const root = document.documentElement;
  const level = s.motionLevel === "" ? "balanced" : s.motionLevel;
  root.setAttribute("data-motion", level === "minimal" ? "off" : "on");
  const mode = s.backgroundMode === "" ? "none" : s.backgroundMode;
  root.setAttribute("data-bg", mode);
  if (mode === "image" && s.backgroundImagePath !== "") {
    const normalized = s.backgroundImagePath.replace(/\\/g, "/");
    const url = normalized.startsWith("/") ? `file://${normalized}` : `file:///${normalized}`;
    root.style.setProperty("--bg-image", `url("${url}")`);
  } else {
    root.style.removeProperty("--bg-image");
  }
  root.style.setProperty("--bg-opacity", `${Math.max(0, Math.min(1, s.backgroundImageOpacity))}`);
  root.style.setProperty("--bg-blur", `${Math.max(0, Math.min(64, s.backgroundBlurRadius))}px`);
  root.setAttribute("data-bg-follow-theme", s.backgroundFollowTheme ? "1" : "0");
}
async function loadAndApplyVisualSettings() {
  try {
    const snap = await request("settings.get");
    applyVisualSettings(snap);
  } catch {
  }
}
function winApi() {
  const w2 = globalThis.win;
  return w2 !== void 0 && typeof w2.close === "function" ? w2 : null;
}
function TitleBar(props) {
  const win = winApi();
  const [maximized, setMaximized] = reactExports.useState(false);
  const [jsDrag, setJsDrag] = reactExports.useState(null);
  const dragFrom = reactExports.useRef(null);
  reactExports.useEffect(() => {
    const probe = document.createElement("div");
    probe.style.setProperty("-webkit-app-region", "drag");
    probe.style.position = "fixed";
    probe.style.left = "-9999px";
    document.body.appendChild(probe);
    const value = getComputedStyle(probe).getPropertyValue("-webkit-app-region");
    setJsDrag(value.trim() === "");
    document.body.removeChild(probe);
  }, []);
  reactExports.useEffect(() => {
    if (win === null) return;
    setMaximized(win.state().maximized);
    const onFocus = () => setMaximized(win.state().maximized);
    window.addEventListener("focus", onFocus);
    return () => window.removeEventListener("focus", onFocus);
  }, [win]);
  const onMouseDown = reactExports.useCallback(
    (e) => {
      if (win === null) return;
      if (e.button !== 0 && e.button !== 1) return;
      if (e.target.closest(".tb-btn, .tb-no-drag") !== null) return;
      if (jsDrag !== true) return;
      dragFrom.current = { x: e.screenX, y: e.screenY };
    },
    [jsDrag, win]
  );
  const onMouseMove = reactExports.useCallback(
    (e) => {
      if (dragFrom.current === null || win === null) return;
      if (win.state().maximized) return;
      const dx = e.screenX - dragFrom.current.x;
      const dy = e.screenY - dragFrom.current.y;
      if (dx === 0 && dy === 0) return;
      dragFrom.current = { x: e.screenX, y: e.screenY };
      win.moveBy(dx, dy);
    },
    [win]
  );
  const endDrag = reactExports.useCallback(() => {
    dragFrom.current = null;
  }, []);
  const maximize = reactExports.useCallback(() => {
    if (win === null) return;
    win.maximize();
    setMaximized(win.state().maximized);
  }, [win]);
  return /* @__PURE__ */ jsxRuntimeExports.jsxs(
    "div",
    {
      className: "tb",
      onMouseDown,
      onMouseMove,
      onMouseUp: endDrag,
      onMouseLeave: endDrag,
      onDoubleClick: maximize,
      children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "tb-lead tb-no-drag", children: props.children }),
        /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { flex: 1 } }),
        props.actions !== void 0 && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "tb-actions", children: props.actions }),
        /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "tb-actions", children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "tb-btn",
              title: "最小化",
              "aria-label": "最小化",
              onClick: () => win?.minimize(),
              children: /* @__PURE__ */ jsxRuntimeExports.jsx("svg", { width: "10", height: "10", viewBox: "0 0 10 10", "aria-hidden": true, children: /* @__PURE__ */ jsxRuntimeExports.jsx("rect", { x: "0", y: "4.5", width: "10", height: "1", fill: "currentColor" }) })
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "tb-btn",
              title: maximized ? "还原" : "最大化",
              "aria-label": maximized ? "还原" : "最大化",
              onClick: maximize,
              children: maximized ? /* @__PURE__ */ jsxRuntimeExports.jsxs("svg", { width: "10", height: "10", viewBox: "0 0 10 10", "aria-hidden": true, children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx("rect", { x: "1", y: "3", width: "6", height: "6", fill: "none", stroke: "currentColor", strokeWidth: "1" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M3 3V1h6v6H7", fill: "none", stroke: "currentColor", strokeWidth: "1" })
              ] }) : /* @__PURE__ */ jsxRuntimeExports.jsx("svg", { width: "10", height: "10", viewBox: "0 0 10 10", "aria-hidden": true, children: /* @__PURE__ */ jsxRuntimeExports.jsx("rect", { x: "1", y: "1", width: "8", height: "8", fill: "none", stroke: "currentColor", strokeWidth: "1" }) })
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "tb-btn tb-close",
              title: "关闭",
              "aria-label": "关闭",
              onClick: () => win?.close(),
              children: /* @__PURE__ */ jsxRuntimeExports.jsx("svg", { width: "10", height: "10", viewBox: "0 0 10 10", "aria-hidden": true, children: /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M0 0 L10 10 M10 0 L0 10", stroke: "currentColor", strokeWidth: "1" }) })
            }
          )
        ] })
      ]
    }
  );
}
const PICKAXE_PATH = "M16,4 H32 V8 H36 V12 H40 V20 H36 V24 H32 V20 H28 V16 H24 V40 H20 V44 H16 V16 H12 V24 H8 V20 H12 V12 H16 Z";
const TIPS = [
  "右键版本列表里的版本，可以「问 Nova」或删除此版本。",
  "Nova 页可以开启游戏内浮窗，局内帮你盯帧数和崩溃。",
  "版本设置里可调内存分配，整合包一般 4-8G 就够。",
  "启动前的自检会自动补全缺失文件，不用手动下载。",
  "Nova 能帮你找整合包和模组，直接说需求就行。",
  "启动出错别慌，错误原因会直接写在启动页上。",
  "删错版本没关系，去下载页重新安装即可恢复。",
  "在「外置」模式填入你自己的第三方认证服务器即可登录。",
  "床可以记录重生点，但在下界会爆炸——可以当 TNT 用。",
  "铁傀儡不怕摔，但会溺水，别让它掉进水里。",
  "苦力怕怕猫：在家附近养猫可以吓退它。",
  "末影人碰水会受伤，也不会传送进水里。",
  "用命名牌在铁砧上改名的生物不会自然消失。",
  "蜂蜜块会减速并减轻摔落伤害，适合做缓冲垫。",
  "灵魂沙水下气泡会把你往上推，是天然电梯。",
  "村民交易会刷新职业等级，升级解锁更好的商品。",
  "内存并非越大越流畅，分配过多反而可能更卡。",
  "Java 版本会按整合包要求自动匹配，无需手动选。",
  "游戏崩溃时先看 .minecraft/logs 里的日志，看不懂问 Nova。",
  "窗口创建失败多半是显卡驱动问题，先更新驱动。",
  "装光影后启动失败，通常是驱动或内存不足导致。",
  "模组多时首次加载较慢，属于正常现象，耐心等待。",
  "大版本更新前记得备份存档，以防万一。",
  "整合包报「模组冲突」可以让 Nova 帮你定位。"
];
function LaunchOverlay(props) {
  const s = props.session;
  const [showLog, setShowLog] = reactExports.useState(false);
  const logRef = reactExports.useRef(null);
  const tip = reactExports.useMemo(() => TIPS[Math.floor(Math.random() * TIPS.length)], [s.launching]);
  const failed = s.launchFailed === true && s.launching === false;
  reactExports.useEffect(() => {
    if (logRef.current !== null) logRef.current.scrollTop = logRef.current.scrollHeight;
  }, [s.logs, showLog]);
  if (!s.launching && !failed) return null;
  const stage = s.stage ?? (failed ? "启动失败" : "正在启动");
  return /* @__PURE__ */ jsxRuntimeExports.jsx(
    "div",
    {
      style: {
        position: "absolute",
        inset: 0,
        zIndex: 500,
        background: "var(--surface-window)",
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
        animation: "fadeIn 180ms cubic-bezier(0.33,0,0.67,1) both"
      },
      children: /* @__PURE__ */ jsxRuntimeExports.jsxs(
        "div",
        {
          style: {
            width: 480,
            maxWidth: "92%",
            display: "flex",
            flexDirection: "column",
            alignItems: "center",
            gap: 14
          },
          children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx("svg", { width: "48", height: "48", viewBox: "0 0 48 48", "aria-hidden": true, children: /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: PICKAXE_PATH, fill: "var(--accent)" }) }),
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { fontSize: 24, fontWeight: 500, textAlign: "center" }, children: failed ? "启动失败" : "正在启动" }),
            /* @__PURE__ */ jsxRuntimeExports.jsx(
              "div",
              {
                className: "t-body truncate",
                style: { maxWidth: "100%", textAlign: "center", color: "var(--text-secondary)" },
                title: props.versionId,
                children: props.versionId ?? ""
              }
            ),
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "progress", style: { width: 340, height: 4 }, children: /* @__PURE__ */ jsxRuntimeExports.jsx(
              "div",
              {
                className: "progress-fill",
                style: { width: `${failed ? 100 : s.progress ?? 6}%` }
              }
            ) }),
            /* @__PURE__ */ jsxRuntimeExports.jsxs(
              "div",
              {
                style: {
                  width: 340,
                  display: "grid",
                  gridTemplateColumns: "auto 1fr",
                  rowGap: 4,
                  columnGap: 8,
                  fontSize: 13
                },
                children: [
                  /* @__PURE__ */ jsxRuntimeExports.jsx("span", { style: { opacity: 0.7, color: "var(--text-secondary)" }, children: "当前步骤" }),
                  /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "truncate", children: stage }),
                  /* @__PURE__ */ jsxRuntimeExports.jsx("span", { style: { opacity: 0.7, color: "var(--text-secondary)" }, children: "登录方式" }),
                  /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "truncate", children: s.loginMode ?? "离线登录" })
                ]
              }
            ),
            /* @__PURE__ */ jsxRuntimeExports.jsxs(
              "div",
              {
                className: "card",
                style: { width: 340, padding: "10px 12px", display: "flex", gap: 10 },
                children: [
                  /* @__PURE__ */ jsxRuntimeExports.jsx(
                    "span",
                    {
                      style: {
                        width: 4,
                        borderRadius: 2,
                        background: failed ? "var(--danger)" : "var(--accent)",
                        flexShrink: 0
                      }
                    }
                  ),
                  /* @__PURE__ */ jsxRuntimeExports.jsx(
                    "span",
                    {
                      style: {
                        fontSize: 12,
                        lineHeight: "18px",
                        color: "var(--text-secondary)",
                        whiteSpace: "pre-wrap"
                      },
                      children: failed ? s.status.replace(/^启动失败：/, "") : tip
                    }
                  )
                ]
              }
            ),
            /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn btn-sm", onClick: () => setShowLog((v2) => !v2), children: showLog ? "隐藏日志" : "显示日志" }),
            showLog && /* @__PURE__ */ jsxRuntimeExports.jsx(
              "div",
              {
                ref: logRef,
                className: "log",
                style: {
                  width: 440,
                  height: 180,
                  background: "var(--surface-subtle)",
                  borderRadius: 8,
                  border: "1px solid var(--stroke-subtle)"
                },
                children: s.logs.length === 0 ? "（暂无输出）" : s.logs.join("\n")
              }
            ),
            /* @__PURE__ */ jsxRuntimeExports.jsx(
              "button",
              {
                className: "btn",
                style: { minWidth: 240 },
                onClick: () => {
                  setShowLog(false);
                  props.onCancel();
                },
                children: failed ? "关闭" : "取消"
              }
            )
          ]
        }
      )
    }
  );
}
function Svg(props) {
  const size = props.size ?? 16;
  return /* @__PURE__ */ jsxRuntimeExports.jsx(
    "svg",
    {
      width: size,
      height: size,
      viewBox: "0 0 24 24",
      fill: props.fill === true ? "currentColor" : "none",
      stroke: props.fill === true ? "none" : "currentColor",
      strokeWidth: props.fill === true ? void 0 : 1.6,
      strokeLinecap: "round",
      strokeLinejoin: "round",
      className: props.className,
      style: { flexShrink: 0, ...props.style },
      "aria-hidden": true,
      children: props.children
    }
  );
}
function IconRefresh(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs(Svg, { ...props, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M20 12a8 8 0 1 1-2.34-5.66" }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M20 4v5h-5" })
  ] });
}
function IconChevronDown(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsx(Svg, { ...props, children: /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M6 9l6 6 6-6" }) });
}
function IconChevronRight(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsx(Svg, { ...props, children: /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M9 6l6 6-6 6" }) });
}
function IconPlay(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsx(Svg, { ...props, fill: true, children: /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M8 5.5v13a1 1 0 0 0 1.53.85l10-6.5a1 1 0 0 0 0-1.7l-10-6.5A1 1 0 0 0 8 5.5z" }) });
}
function IconClose(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsx(Svg, { ...props, children: /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M6 6l12 12M18 6L6 18" }) });
}
function IconMore(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs(Svg, { ...props, fill: true, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("circle", { cx: "12", cy: "5", r: "1.6" }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("circle", { cx: "12", cy: "12", r: "1.6" }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("circle", { cx: "12", cy: "19", r: "1.6" })
  ] });
}
function IconTrash(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs(Svg, { ...props, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M4 7h16M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2" }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12" }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M10 11v6M14 11v6" })
  ] });
}
function IconFolder(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsx(Svg, { ...props, children: /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M3 7.5A1.5 1.5 0 0 1 4.5 6h4l2 2.5h8.5A1.5 1.5 0 0 1 20.5 10v7a1.5 1.5 0 0 1-1.5 1.5H4.5A1.5 1.5 0 0 1 3 17z" }) });
}
function IconImport(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs(Svg, { ...props, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M12 4v9" }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M8.5 9.5L12 13l3.5-3.5" }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M4 15v3a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-3" })
  ] });
}
function IconLink(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs(Svg, { ...props, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M10.5 13.5a3.5 3.5 0 0 0 5 0l2.5-2.5a3.5 3.5 0 0 0-5-5L11.8 7" }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M13.5 10.5a3.5 3.5 0 0 0-5 0L6 13a3.5 3.5 0 0 0 5 5l1.2-1.2" })
  ] });
}
function IconList(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs(Svg, { ...props, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M8 6h12M8 12h12M8 18h12" }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M4 6h.01M4 12h.01M4 18h.01" })
  ] });
}
function IconHome(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs(Svg, { ...props, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M4 10.5L12 4l8 6.5V19a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 4 19z" }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M9.5 20.5v-6h5v6" })
  ] });
}
function IconSettings(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs(Svg, { ...props, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("circle", { cx: "12", cy: "12", r: "3" }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M19.4 15a1.7 1.7 0 0 0 .34 1.87l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.7 1.7 0 0 0-2.87 1.2V21a2 2 0 1 1-4 0v-.09A1.7 1.7 0 0 0 7 19.4a1.7 1.7 0 0 0-1.87.34l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06A1.7 1.7 0 0 0 2.6 15H2.5a2 2 0 1 1 0-4h.09A1.7 1.7 0 0 0 4.6 9a1.7 1.7 0 0 0-.34-1.87l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06A1.7 1.7 0 0 0 9 4.6h.09A1.7 1.7 0 0 0 10.3 2.6V2.5a2 2 0 1 1 4 0v.09A1.7 1.7 0 0 0 15 4.6a1.7 1.7 0 0 0 1.87-.34l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06A1.7 1.7 0 0 0 19.4 9v.09a1.7 1.7 0 0 0 1.51 1.51h.09a2 2 0 1 1 0 4h-.09a1.7 1.7 0 0 0-1.51 1.4z" })
  ] });
}
function IconUser(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs(Svg, { ...props, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("circle", { cx: "12", cy: "8", r: "3.6" }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M4.5 20.5c0-3.6 3.4-6 7.5-6s7.5 2.4 7.5 6" })
  ] });
}
function IconSparkle(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs(Svg, { ...props, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M12 3.5l1.9 4.6 4.6 1.9-4.6 1.9L12 16.5l-1.9-4.6L5.5 10l4.6-1.9z" }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M18 17l.9 2.1L21 20l-2.1.9L18 23l-.9-2.1L15 20l2.1-.9z" })
  ] });
}
function IconCheck(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsx(Svg, { ...props, children: /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M5 12.5l4.5 4.5L19 7.5" }) });
}
function IconWarning(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs(Svg, { ...props, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M12 4.5l8.5 15H3.5z" }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M12 10v4M12 17h.01" })
  ] });
}
function IconInfo(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs(Svg, { ...props, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("circle", { cx: "12", cy: "12", r: "8.5" }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M12 11v5M12 8h.01" })
  ] });
}
function IconPlus(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsx(Svg, { ...props, children: /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M12 5v14M5 12h14" }) });
}
function IconCube(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs(Svg, { ...props, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M12 3.5l8 4.2v8.6l-8 4.2-8-4.2V7.7z" }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M4 7.7l8 4.3 8-4.3M12 12v8.5" })
  ] });
}
function IconWorld(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs(Svg, { ...props, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("rect", { x: "3.5", y: "3.5", width: "17", height: "17", rx: "2" }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M3.5 9.5h17M3.5 15h17M9.5 3.5v17M15 3.5v17" })
  ] });
}
function IconTasks(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs(Svg, { ...props, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M4 6.5h10M4 12h16M4 17.5h16" }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("path", { d: "M4 6.5h.01" })
  ] });
}
function PageShell(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page", children: [
    /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page-head", children: [
      /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minWidth: 0 }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx("h1", { className: "page-title", children: props.title }),
        props.subtitle !== void 0 && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "page-sub", children: props.subtitle })
      ] }),
      props.actions
    ] }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "page-body", children: props.children })
  ] });
}
function EmptyState(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "empty", children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "empty-title", children: props.title }),
    props.hint !== void 0 && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "empty-hint", children: props.hint }),
    props.action
  ] });
}
function SkeletonList(props) {
  const rows = props.rows ?? 5;
  return /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { animation: "fadeIn 180ms cubic-bezier(0.33, 0, 0.67, 1) both" }, children: Array.from({ length: rows }).map((_, index) => /* @__PURE__ */ jsxRuntimeExports.jsxs(
    "div",
    {
      className: "skel-row",
      style: { minHeight: props.tall === true ? 72 : 44 },
      children: [
        props.avatar === true && /* @__PURE__ */ jsxRuntimeExports.jsx(
          "div",
          {
            className: "skel-thumb",
            style: props.tall === true ? { width: 64, height: 64, borderRadius: 8 } : void 0
          }
        ),
        /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "skel-text", style: { maxWidth: `${60 - index * 6}%` } })
      ]
    },
    index
  )) });
}
const TOAST_ICON = {
  success: /* @__PURE__ */ jsxRuntimeExports.jsx(IconCheck, { size: 14 }),
  error: /* @__PURE__ */ jsxRuntimeExports.jsx(IconClose, { size: 14 }),
  info: /* @__PURE__ */ jsxRuntimeExports.jsx(IconInfo, { size: 14 }),
  warning: /* @__PURE__ */ jsxRuntimeExports.jsx(IconWarning, { size: 14 })
};
function InfoBar(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: `info info-${props.kind ?? "info"}`, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("span", { "aria-hidden": true, children: TOAST_ICON[props.kind ?? "info"] }),
    /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "info-body", children: [
      props.title !== void 0 && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "info-title", children: props.title }),
      props.children
    ] }),
    props.onDismiss !== void 0 && /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn btn-subtle btn-sm", onClick: props.onDismiss, children: /* @__PURE__ */ jsxRuntimeExports.jsx(IconClose, { size: 13 }) })
  ] });
}
function useToasts() {
  const [items, setItems] = reactExports.useState([]);
  const seq = reactExports.useRef(1);
  const push = reactExports.useCallback((kind, text) => {
    const id2 = seq.current++;
    setItems((prev) => [...prev, { id: id2, kind, text }]);
    setTimeout(() => setItems((prev) => prev.filter((t2) => t2.id !== id2)), 4200);
  }, []);
  const host = /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "toast-host", children: items.map((item) => /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card toast info info-{item.kind}", children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("span", { "aria-hidden": true, children: TOAST_ICON[item.kind] }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "info-body", children: item.text }),
    /* @__PURE__ */ jsxRuntimeExports.jsx(
      "button",
      {
        className: "btn btn-subtle btn-sm",
        onClick: () => setItems((prev) => prev.filter((t2) => t2.id !== item.id)),
        children: /* @__PURE__ */ jsxRuntimeExports.jsx(IconClose, { size: 13 })
      }
    )
  ] }, item.id)) });
  return { push, host };
}
function Toggle(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsx(
    "button",
    {
      className: "toggle",
      role: "switch",
      "aria-checked": props.checked,
      "aria-label": props.label,
      disabled: props.disabled,
      onClick: () => props.onChange(!props.checked)
    }
  );
}
function Segmented(props) {
  const n2 = props.options.length;
  const index = Math.max(
    0,
    props.options.findIndex((o) => o.value === props.value)
  );
  const slot = 100 / n2;
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "seg", style: { gridTemplateColumns: `repeat(${n2}, 1fr)` }, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx(
      "div",
      {
        className: "seg-ind",
        style: {
          width: `calc(${slot}% - 2px)`,
          // translateX 的百分比相对「指示器自身宽度」（恰好一格），
          // 所以每前进一格 = +100%，再补格子之间的 2px 间距
          transform: `translateX(calc(2px + ${index * 100}% + ${index * 2}px))`
        }
      }
    ),
    props.options.map((o) => /* @__PURE__ */ jsxRuntimeExports.jsx(
      "button",
      {
        className: `seg-item${o.value === props.value ? " seg-active" : ""}`,
        disabled: props.disabled,
        onClick: () => props.onChange(o.value),
        children: o.label
      },
      o.value
    ))
  ] });
}
function ProgressBar(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: `progress${props.indeterminate === true ? " progress-indeterminate" : ""}`, children: props.value !== void 0 && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "progress-fill", style: { width: `${Math.max(0, Math.min(100, props.value))}%` } }) });
}
function useContextMenu(items) {
  const [pos, setPos] = reactExports.useState(null);
  const ref = reactExports.useRef(null);
  const itemsRef = reactExports.useRef(items);
  itemsRef.current = items;
  reactExports.useEffect(() => {
    if (pos === null) return;
    const onDown = (e) => {
      if (ref.current !== null && !ref.current.contains(e.target)) {
        setPos(null);
      }
    };
    const onKey = (e) => {
      if (e.key === "Escape") setPos(null);
    };
    const onResize = () => setPos(null);
    window.addEventListener("mousedown", onDown);
    window.addEventListener("keydown", onKey);
    window.addEventListener("resize", onResize);
    return () => {
      window.removeEventListener("mousedown", onDown);
      window.removeEventListener("keydown", onKey);
      window.removeEventListener("resize", onResize);
    };
  }, [pos]);
  const width = 200;
  const height = items.length * 32 + 12;
  const flipX = pos !== null && pos.x > window.innerWidth - width - 12;
  const flipY = pos !== null && pos.y > window.innerHeight - height - 12;
  const node = pos === null ? null : /* @__PURE__ */ jsxRuntimeExports.jsx(
    "div",
    {
      ref,
      className: "flyout",
      style: {
        position: "fixed",
        left: flipX ? pos.x - width : pos.x,
        top: flipY ? pos.y - height : pos.y,
        width,
        zIndex: 800,
        display: "flex",
        flexDirection: "column",
        gap: 2,
        padding: 6
      },
      children: items.map((item) => /* @__PURE__ */ jsxRuntimeExports.jsx(
        "button",
        {
          className: `nav-item${item.danger === true ? " btn-danger" : ""}`,
          style: { height: 32, width: "100%", fontSize: 13 },
          disabled: item.disabled === true,
          onClick: () => {
            setPos(null);
            item.onSelect();
          },
          children: item.label
        },
        item.label
      ))
    }
  );
  return {
    bind: () => ({
      onContextMenu: (e) => {
        e.preventDefault();
        setPos({ x: e.clientX, y: e.clientY });
      },
      style: {}
    }),
    close: () => setPos(null),
    node
  };
}
function Modal(props) {
  if (!props.open) return null;
  return /* @__PURE__ */ jsxRuntimeExports.jsx(
    "div",
    {
      className: "scrim",
      onClick: (e) => {
        if (e.target === e.currentTarget) props.onClose();
      },
      children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "dialog", style: { width: props.width ?? 480 }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "dialog-title", children: props.title }),
        /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "dialog-body", children: props.children }),
        props.footer !== void 0 && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "dialog-actions", children: props.footer })
      ] })
    }
  );
}
class ErrorBoundary extends React.Component {
  constructor(props) {
    super(props);
    this.state = {};
  }
  static getDerivedStateFromError(error) {
    return { error };
  }
  render() {
    if (this.state.error !== void 0) {
      return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "empty", style: { paddingTop: 60 }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "empty-title", children: [
          this.props.label,
          " 页面出错了"
        ] }),
        /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "empty-hint", children: String(this.state.error) }),
        /* @__PURE__ */ jsxRuntimeExports.jsx(
          "button",
          {
            className: "btn btn-primary",
            onClick: () => this.setState({ error: void 0 }),
            children: "重试"
          }
        )
      ] });
    }
    return this.props.children;
  }
}
function Select(props) {
  const [open, setOpen] = reactExports.useState(false);
  const [rect, setRect] = reactExports.useState(
    null
  );
  const rootRef = reactExports.useRef(null);
  const popRef = reactExports.useRef(null);
  reactExports.useEffect(() => {
    if (!open) return;
    const onDown = (e) => {
      const t2 = e.target;
      if (rootRef.current?.contains(t2) === true) return;
      if (popRef.current?.contains(t2) === true) return;
      setOpen(false);
    };
    const onKey = (e) => {
      if (e.key === "Escape") setOpen(false);
    };
    const close = () => setOpen(false);
    window.addEventListener("mousedown", onDown);
    window.addEventListener("keydown", onKey);
    window.addEventListener("resize", close);
    window.addEventListener("scroll", close, true);
    return () => {
      window.removeEventListener("mousedown", onDown);
      window.removeEventListener("keydown", onKey);
      window.removeEventListener("resize", close);
      window.removeEventListener("scroll", close, true);
    };
  }, [open]);
  const current = props.options.find((o) => o.value === props.value);
  const toggle = () => {
    if (props.disabled === true) return;
    if (open) {
      setOpen(false);
      return;
    }
    const r2 = rootRef.current?.getBoundingClientRect();
    if (r2 === void 0) return;
    const itemH = 34;
    const estimated = Math.min(320, props.options.length * itemH + 12);
    setRect({
      left: r2.left,
      top: r2.bottom + 6,
      width: r2.width,
      up: window.innerHeight - r2.bottom < estimated + 16
    });
    setOpen(true);
  };
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { ref: rootRef, style: { width: props.width ?? 220 }, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsxs(
      "button",
      {
        className: "input",
        style: {
          width: "100%",
          display: "flex",
          alignItems: "center",
          gap: 8,
          cursor: props.disabled === true ? "default" : "pointer",
          opacity: props.disabled === true ? 0.5 : 1,
          textAlign: "left"
        },
        disabled: props.disabled,
        title: current?.label ?? props.placeholder ?? "",
        onClick: toggle,
        children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "truncate", style: { flex: 1 }, children: current?.label ?? props.placeholder ?? "" }),
          /* @__PURE__ */ jsxRuntimeExports.jsx(IconChevronDown, { size: 14, style: { opacity: 0.6 } })
        ]
      }
    ),
    open && rect !== null && reactDomExports.createPortal(
      /* @__PURE__ */ jsxRuntimeExports.jsx(
        "div",
        {
          ref: popRef,
          className: "flyout",
          style: {
            position: "fixed",
            left: rect.left,
            width: rect.width,
            ...rect.up ? { bottom: window.innerHeight - rect.top + 12 } : { top: rect.top },
            maxHeight: 320,
            overflowY: "auto",
            zIndex: 900,
            padding: 6,
            // 不透明底：半透明 + 大模糊会把下面的文字透上来，读不清
            background: "var(--surface-window)"
          },
          children: props.options.map((o) => /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: `nav-item${o.value === props.value ? " nav-active" : ""}`,
              style: { height: 32, width: "100%", fontSize: 13, flexShrink: 0 },
              onClick: () => {
                props.onChange(o.value);
                setOpen(false);
              },
              children: /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "truncate", children: o.label })
            },
            o.value
          ))
        }
      ),
      document.body
    )
  ] });
}
const scriptRel = function detectScriptRel() {
  const relList = typeof document !== "undefined" && document.createElement("link").relList;
  return relList && relList.supports && relList.supports("modulepreload") ? "modulepreload" : "preload";
}();
const assetsURL = function(dep, importerUrl) {
  return new URL(dep, importerUrl).href;
};
const seen = {};
const __vitePreload = function preload(baseModule, deps, importerUrl) {
  let promise = Promise.resolve();
  if (deps && deps.length > 0) {
    const links = document.getElementsByTagName("link");
    const cspNonceMeta = document.querySelector(
      "meta[property=csp-nonce]"
    );
    const cspNonce = cspNonceMeta?.nonce || cspNonceMeta?.getAttribute("nonce");
    promise = Promise.allSettled(
      deps.map((dep) => {
        dep = assetsURL(dep, importerUrl);
        if (dep in seen) return;
        seen[dep] = true;
        const isCss = dep.endsWith(".css");
        const cssSelector = isCss ? '[rel="stylesheet"]' : "";
        const isBaseRelative = !!importerUrl;
        if (isBaseRelative) {
          for (let i = links.length - 1; i >= 0; i--) {
            const link2 = links[i];
            if (link2.href === dep && (!isCss || link2.rel === "stylesheet")) {
              return;
            }
          }
        } else if (document.querySelector(`link[href="${dep}"]${cssSelector}`)) {
          return;
        }
        const link = document.createElement("link");
        link.rel = isCss ? "stylesheet" : scriptRel;
        if (!isCss) {
          link.as = "script";
        }
        link.crossOrigin = "";
        link.href = dep;
        if (cspNonce) {
          link.setAttribute("nonce", cspNonce);
        }
        document.head.appendChild(link);
        if (isCss) {
          return new Promise((res, rej) => {
            link.addEventListener("load", res);
            link.addEventListener(
              "error",
              () => rej(new Error(`Unable to preload CSS for ${dep}`))
            );
          });
        }
      })
    );
  }
  function handlePreloadError(err) {
    const e = new Event("vite:preloadError", {
      cancelable: true
    });
    e.payload = err;
    window.dispatchEvent(e);
    if (!e.defaultPrevented) {
      throw err;
    }
  }
  return promise.then((res) => {
    for (const item of res || []) {
      if (item.status !== "rejected") continue;
      handlePreloadError(item.reason);
    }
    return baseModule().catch(handlePreloadError);
  });
};
function useGameSession() {
  const [store, setStore] = reactExports.useState({
    launching: false,
    status: "",
    logs: []
  });
  const [selectedVersion, setVersion] = reactExports.useState();
  const startedRef = reactExports.useRef(false);
  reactExports.useEffect(() => {
    if (startedRef.current) return;
    startedRef.current = true;
    onBridgeEvent((event, params) => {
      if (event === "game.output") {
        const line = String(params.line ?? "");
        setStore((s) => ({ ...s, logs: [...s.logs, line].slice(-600) }));
      } else if (event === "game.exited") {
        const code = Number(params.code ?? -1);
        setStore((s) => ({
          ...s,
          pid: void 0,
          sessionId: void 0,
          launching: false,
          exitCode: code,
          status: `游戏退出（exit=${code}）`
        }));
      }
    });
  }, []);
  return {
    ...store,
    selectedVersion,
    setVersion,
    clearLogs: () => setStore((s) => ({ ...s, logs: [] })),
    dismissLaunchError: () => setStore((s) => ({ ...s, launchFailed: false, status: "", stage: void 0, progress: void 0 })),
    launch: async ({ versionId, playerName, accountUuid, loginMode }) => {
      if (startedRef.current === false) return;
      const step = (stage, progress) => setStore((s) => ({ ...s, stage, progress, launching: true }));
      setStore((s) => ({
        ...s,
        launching: true,
        launchFailed: false,
        logs: [],
        status: "启动中…",
        launchVersion: versionId,
        stage: "检查运行环境…",
        progress: 8,
        loginMode: loginMode ?? ""
      }));
      const { request: request2 } = await __vitePreload(async () => {
        const { request: request3 } = await Promise.resolve().then(() => bridge);
        return { request: request3 };
      }, true ? void 0 : void 0, import.meta.url);
      try {
        step("准备游戏文件…", 35);
        const session = await request2("launch.start", {
          versionId,
          playerName,
          accountUuid
        });
        step("游戏进程已启动", 100);
        setStore((s) => ({
          ...s,
          launching: false,
          sessionId: session.sessionId,
          pid: session.pid,
          exitCode: void 0,
          launchFailed: false,
          stage: void 0,
          progress: void 0,
          status: `游戏运行中（pid ${session.pid}）`
        }));
      } catch (e) {
        const message = String(e);
        setStore((s) => ({
          ...s,
          launching: false,
          launchFailed: true,
          stage: void 0,
          progress: void 0,
          status: `启动失败：${message}`
        }));
      }
    }
  };
}
function now() {
  const d = /* @__PURE__ */ new Date();
  const p2 = (n2) => n2.toString().padStart(2, "0");
  return `${p2(d.getHours())}:${p2(d.getMinutes())}:${p2(d.getSeconds())}`;
}
function useTaskCenter() {
  const [tasks, setTasks] = reactExports.useState([]);
  const [notifications, setNotifications] = reactExports.useState([]);
  const seqRef = reactExports.useRef(1);
  const notify = reactExports.useCallback((level, message) => {
    setNotifications(
      (prev) => [{ id: seqRef.current++, time: now(), level, message }, ...prev].slice(0, 100)
    );
  }, []);
  reactExports.useEffect(() => {
    let alive = true;
    const upsert = (id2, patch) => {
      setTasks((prev) => {
        const idx = prev.findIndex((t2) => t2.id === id2);
        if (idx < 0) return prev;
        const next = [...prev];
        next[idx] = { ...next[idx], ...patch };
        return next;
      });
    };
    onBridgeEvent((event, params) => {
      if (!alive) return;
      if (event === "install.progress") {
        const id2 = String(params.versionId ?? params.projectId ?? "");
        if (id2 === "") return;
        setTasks((prev) => {
          const exists = prev.some((t2) => t2.id === id2);
          const base = {
            id: id2,
            kind: "download",
            title: id2,
            status: String(params.stage ?? ""),
            progress: Number(params.total ?? 0) > 0 ? Number(params.current ?? 0) / Number(params.total) * 100 : void 0,
            startedAt: Date.now()
          };
          return exists ? prev.map(
            (t2) => t2.id === id2 ? { ...t2, status: base.status, progress: base.progress } : t2
          ) : [base, ...prev];
        });
      } else if (event === "install.done") {
        const id2 = String(params.versionId ?? params.projectId ?? "");
        upsert(id2, { finished: true, status: "已完成", progress: 100 });
        notify("信息", `${id2} 安装完成`);
      } else if (event === "install.failed") {
        const id2 = String(params.versionId ?? params.projectId ?? "");
        upsert(id2, { finished: true, failed: true, status: "失败" });
        notify("错误", `${id2} 安装失败：${String(params.error ?? "")}`);
      } else if (event === "game.exited") {
        const code = Number(params.code ?? -1);
        notify(
          code === 0 ? "信息" : "错误",
          `游戏退出（exit=${code}）`
        );
      }
    });
    return () => {
      alive = false;
    };
  }, [notify]);
  const clearNotifications = reactExports.useCallback(() => setNotifications([]), []);
  return { tasks, notifications, clearNotifications };
}
const grassIcon = "" + new URL("natural_grass_block-CHp-IFT_.png", import.meta.url).href;
const amethystIcon = "" + new URL("block_of_amethyst-CNDtZttz.png", import.meta.url).href;
const cobbleIcon = "" + new URL("masonry_cobblestone-DZ8K_SH1.png", import.meta.url).href;
const dirtIcon = "" + new URL("natural_dirt-Ca2Zi6Hf.png", import.meta.url).href;
const neoforgeIcon = "" + new URL("neoforge-Cs6rLB_n.png", import.meta.url).href;
const forgeIcon = "" + new URL("forge-BcP8Uij_.png", import.meta.url).href;
const fabricIcon = "" + new URL("fabric-BaGEkOoB.png", import.meta.url).href;
const quiltIcon = "" + new URL("quilt-CJezQSVt.png", import.meta.url).href;
const MODE_LABEL = {
  microsoft: "正版",
  external: "外置",
  offline: "离线"
};
const LOADER_ICONS$1 = {
  neoforge: neoforgeIcon,
  forge: forgeIcon,
  fabric: fabricIcon,
  quilt: quiltIcon
};
const TYPE_ICONS$1 = {
  release: grassIcon,
  snapshot: amethystIcon,
  old_beta: cobbleIcon,
  old_alpha: dirtIcon
};
function iconFor$2(version) {
  const loader = version.loader ?? "";
  if (loader !== "" && LOADER_ICONS$1[loader] !== void 0) return LOADER_ICONS$1[loader];
  const lower = version.id.toLowerCase();
  if (lower.includes("neoforge")) return neoforgeIcon;
  if (lower.includes("forge")) return forgeIcon;
  if (lower.includes("fabric")) return fabricIcon;
  if (lower.includes("quilt")) return quiltIcon;
  const type = version.type ?? (lower.includes("-rc") || lower.includes("-pre") ? "snapshot" : "release");
  return TYPE_ICONS$1[type] ?? grassIcon;
}
function tagFor$1(version) {
  const loader = version.loader ?? "";
  const LOADER_LABEL2 = {
    neoforge: "NeoForge",
    forge: "Forge",
    fabric: "Fabric",
    quilt: "Quilt"
  };
  if (loader !== "" && LOADER_LABEL2[loader] !== void 0) return LOADER_LABEL2[loader];
  const lower = version.id.toLowerCase();
  if (lower.includes("neoforge")) return "NeoForge";
  if (lower.includes("fabric")) return "Fabric";
  if (lower.includes("quilt")) return "Quilt";
  if (lower.includes("forge")) return "Forge";
  const type = version.type ?? (lower.includes("-rc") || lower.includes("-pre") ? "snapshot" : "release");
  const TYPE_LABEL2 = {
    release: "正式版",
    snapshot: "快照版",
    old_beta: "远古 Beta",
    old_alpha: "远古 Alpha"
  };
  return TYPE_LABEL2[type] ?? "正式版";
}
function lastPlayed$1(iso) {
  if (iso === void 0 || iso === null || iso === "") return "从未启动";
  const d = new Date(iso);
  if (isNaN(d.getTime())) return "从未启动";
  const pad = (n2) => n2.toString().padStart(2, "0");
  const hm = pad(d.getHours()) + ":" + pad(d.getMinutes());
  const today = /* @__PURE__ */ new Date();
  const dayDiff = Math.floor(
    (new Date(today.getFullYear(), today.getMonth(), today.getDate()).getTime() - new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime()) / 864e5
  );
  if (dayDiff <= 0) return "今天 " + hm;
  if (dayDiff === 1) return "昨天 " + hm;
  if (dayDiff < 30) return dayDiff + " 天前";
  if (dayDiff < 365) return Math.floor(dayDiff / 30) + " 个月前";
  return d.getFullYear() + "-" + pad(d.getMonth() + 1) + "-" + pad(d.getDate());
}
function sortVersions$1(list) {
  return [...list].sort((a, b) => {
    const aLast = a.isDefault === true ? 1 : 0;
    const bLast = b.isDefault === true ? 1 : 0;
    if (aLast !== bLast) return bLast - aLast;
    const ai2 = a.id.toLowerCase();
    const bi2 = b.id.toLowerCase();
    return ai2 < bi2 ? 1 : ai2 > bi2 ? -1 : 0;
  });
}
function fmtSize$3(bytes) {
  if (!bytes) return "未完成";
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}
function HomePage(props) {
  const session = props.session;
  const { push, host } = useToasts();
  const [installed, setInstalled] = reactExports.useState([]);
  const [accounts, setAccounts] = reactExports.useState([]);
  const [selected, setSelected] = reactExports.useState();
  const [mode, setMode] = reactExports.useState("offline");
  const [error, setError] = reactExports.useState("");
  const [loading, setLoading] = reactExports.useState(true);
  const [gameDir, setGameDir] = reactExports.useState("");
  const [avatarFailed, setAvatarFailed] = reactExports.useState(false);
  async function reload() {
    setLoading(true);
    try {
      const catalog = await request("catalog.installed");
      setInstalled(sortVersions$1(catalog.installed));
      setSelected((prev) => {
        if (prev !== void 0 && catalog.installed.some((v2) => v2.id === prev)) return prev;
        return catalog.installed.find((v2) => v2.isDefault)?.id ?? catalog.installed[0]?.id;
      });
    } catch (e) {
      setError((prev) => prev === "" ? String(e) : `${prev}；${String(e)}`);
    }
    try {
      const list = await request("accounts.list");
      setAccounts(list.accounts);
      const cur = list.accounts.find((a) => a.uuid === list.current) ?? list.accounts[0];
      if (cur !== void 0) {
        setMode(
          cur.type === "microsoft" ? "microsoft" : cur.type === "external" ? "external" : "offline"
        );
      }
    } catch (e) {
      setError((prev) => prev === "" ? String(e) : `${prev}；${String(e)}`);
    }
    try {
      const settings = await request("settings.get");
      setGameDir(settings.gameDir);
    } catch {
    }
    setLoading(false);
  }
  reactExports.useEffect(() => {
    void reload();
  }, []);
  const modeAccounts = accounts.filter(
    (a) => mode === "offline" ? a.type === "offline" : mode === "microsoft" ? a.type === "microsoft" : a.type === "external"
  );
  const account = modeAccounts[0];
  const avatarId = account === void 0 ? "" : account.type === "microsoft" ? account.uuid : account.name;
  const avatarUrl2 = avatarId === "" ? null : "https://minotar.net/helm/" + encodeURIComponent(avatarId) + "/128";
  const running = session.pid !== void 0 && session.pid > 0;
  const canLaunch = !session.launching && !running && selected !== void 0 && account !== void 0;
  const menu = useContextMenu([
    { label: "问 Nova", onSelect: () => props.onNavigate("nova") },
    { label: "版本详情", onSelect: () => props.onNavigate("versions") },
    {
      label: "打开游戏目录",
      onSelect: () => {
        if (selected === void 0) return;
        void request("versions.openFolder", { versionId: selected }).catch(
          (e) => push("error", String(e))
        );
      }
    },
    {
      label: "删除此版本",
      danger: true,
      onSelect: () => {
        if (selected === void 0) return;
        const target = selected;
        void request("versions.delete", { versionId: target }).then((r2) => {
          push("success", `已删除 ${target}（释放 ${fmtSize$3(r2.freedBytes)}）`);
          return reload();
        }).catch((e) => push("error", String(e)));
      }
    }
  ]);
  const statusColor = running ? "var(--success)" : session.launching ? "var(--warning)" : session.status.startsWith("启动失败") ? "var(--danger)" : "var(--text-disabled)";
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page", children: [
    error !== "" && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { marginBottom: 12, flexShrink: 0 }, children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "info info-danger", children: [
      /* @__PURE__ */ jsxRuntimeExports.jsx(IconClose, { size: 14 }),
      /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "info-body", children: error })
    ] }) }),
    /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minHeight: 0, display: "flex", gap: 24 }, children: [
      /* @__PURE__ */ jsxRuntimeExports.jsx(
        "div",
        {
          style: { flex: 1, minWidth: 0, minHeight: 0, display: "flex", flexDirection: "column" },
          children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card card-flush", style: { flex: 1, minHeight: 0 }, children: [
            /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card-head", children: [
              /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "card-title", children: "已安装版本" }),
              /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "chip", children: installed.length }),
              /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { flex: 1 } }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs(
                "button",
                {
                  className: "btn btn-subtle btn-sm",
                  onClick: () => void reload(),
                  children: [
                    /* @__PURE__ */ jsxRuntimeExports.jsx(IconRefresh, { size: 14 }),
                    " 刷新"
                  ]
                }
              )
            ] }),
            /* @__PURE__ */ jsxRuntimeExports.jsxs(
              "div",
              {
                style: {
                  flex: 1,
                  minHeight: 0,
                  overflowY: "auto",
                  overflowX: "hidden",
                  padding: "0 10px 10px"
                },
                children: [
                  loading && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { padding: 20, textAlign: "center", opacity: 0.6 }, children: "加载中…" }),
                  !loading && installed.length === 0 && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "empty", children: [
                    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "empty-title", children: "还没有已安装的版本" }),
                    /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "empty-hint", children: [
                      "去「下载」页选择一个版本安装。",
                      gameDir !== "" && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { children: [
                        "当前游戏目录：",
                        gameDir
                      ] })
                    ] }),
                    /* @__PURE__ */ jsxRuntimeExports.jsx(
                      "button",
                      {
                        className: "btn btn-primary",
                        onClick: () => props.onNavigate("download"),
                        children: "前往下载"
                      }
                    )
                  ] }),
                  installed.map((version, index) => {
                    const isSelected = version.id === selected;
                    const icon = iconFor$2(version);
                    return /* @__PURE__ */ jsxRuntimeExports.jsx("div", { ...menu.bind(), children: /* @__PURE__ */ jsxRuntimeExports.jsxs(
                      "div",
                      {
                        className: `row${isSelected ? " row-active" : ""}`,
                        style: {
                          animationDelay: `${index * 39}ms`,
                          // Win 端列宽：图标 Auto / 名称 260 / 芯片 * / 数据 Auto
                          display: "grid",
                          gridTemplateColumns: "auto minmax(120px, 260px) minmax(0, 1fr) auto",
                          columnGap: 12
                        },
                        onClick: () => setSelected(version.id),
                        children: [
                          /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "icon32", children: /* @__PURE__ */ jsxRuntimeExports.jsx("img", { src: icon, alt: "", width: 32, height: 32 }) }),
                          /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-strong truncate", children: version.id }),
                          /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 6, alignItems: "center" }, children: [
                            /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "chip", children: tagFor$1(version) }),
                            version.isDefault === true && /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "chip chip-accent", children: "默认" })
                          ] }),
                          /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "meta", children: [
                            /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "meta-k", children: "上次启动" }),
                            /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "meta-v", children: lastPlayed$1(version.lastPlayedAt) }),
                            /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "meta-k", children: "大小" }),
                            /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "meta-v", children: fmtSize$3(version.sizeBytes) })
                          ] })
                        ]
                      }
                    ) }, version.id);
                  })
                ]
              }
            )
          ] })
        }
      ),
      /* @__PURE__ */ jsxRuntimeExports.jsx(
        "div",
        {
          style: {
            // 宽屏 360；窄屏（最小窗口 1000 / 手机横屏）按视口比例收缩，
            // 固定 360 会把左栏压到只剩几百像素（响应式见 winui.css 的 .home-side）
            width: "clamp(240px, 28vw, 360px)",
            minWidth: 0,
            flexShrink: 0,
            minHeight: 0,
            display: "flex"
          },
          children: /* @__PURE__ */ jsxRuntimeExports.jsxs(
            "div",
            {
              className: "card",
              style: {
                flex: 1,
                minWidth: 0,
                overflow: "hidden",
                display: "flex",
                flexDirection: "column",
                gap: 14
              },
              children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Segmented,
                  {
                    value: mode,
                    options: [
                      { value: "microsoft", label: MODE_LABEL.microsoft },
                      { value: "external", label: MODE_LABEL.external },
                      { value: "offline", label: MODE_LABEL.offline }
                    ],
                    onChange: setMode
                  }
                ),
                /* @__PURE__ */ jsxRuntimeExports.jsxs(
                  "div",
                  {
                    style: {
                      flex: 1,
                      display: "flex",
                      flexDirection: "column",
                      alignItems: "center",
                      justifyContent: "center",
                      gap: 10
                    },
                    children: [
                      /* @__PURE__ */ jsxRuntimeExports.jsx(
                        "div",
                        {
                          style: {
                            width: 88,
                            height: 88,
                            borderRadius: "var(--r-control)",
                            background: account === void 0 ? "var(--surface-hover)" : "linear-gradient(180deg,#3a2a1a,#241a10)",
                            display: "flex",
                            alignItems: "center",
                            justifyContent: "center",
                            fontSize: 40,
                            color: account === void 0 ? "var(--text-tertiary)" : "#e0a76a",
                            overflow: "hidden",
                            animation: session.launching ? "pulse 1.4s infinite" : void 0
                          },
                          children: avatarUrl2 !== null && !avatarFailed ? /* @__PURE__ */ jsxRuntimeExports.jsx(
                            "img",
                            {
                              src: avatarUrl2,
                              alt: account?.name ?? "",
                              width: 88,
                              height: 88,
                              style: { objectFit: "cover" },
                              onError: () => setAvatarFailed(true)
                            }
                          ) : account?.name[0]?.toUpperCase() ?? /* @__PURE__ */ jsxRuntimeExports.jsx(IconUser, { size: 36 })
                        }
                      ),
                      /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { fontSize: 15, fontWeight: 600 }, children: account?.name ?? "未选择账户" }),
                      account === void 0 && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-cap", children: [
                        "去「账户」页添加",
                        MODE_LABEL[mode],
                        "账户"
                      ] }),
                      account !== void 0 && modeAccounts.length > 1 && /* @__PURE__ */ jsxRuntimeExports.jsx(
                        "div",
                        {
                          style: {
                            display: "flex",
                            gap: 6,
                            flexWrap: "wrap",
                            justifyContent: "center"
                          },
                          children: modeAccounts.map((a) => /* @__PURE__ */ jsxRuntimeExports.jsx(
                            "button",
                            {
                              className: "btn btn-subtle btn-sm",
                              onClick: () => void request("accounts.setCurrent", { uuid: a.uuid }),
                              children: a.name
                            },
                            a.uuid
                          ))
                        }
                      )
                    ]
                  }
                ),
                session.status !== "" && /* @__PURE__ */ jsxRuntimeExports.jsxs(
                  "div",
                  {
                    style: { display: "flex", alignItems: "center", gap: 8, minWidth: 0 },
                    title: session.status,
                    children: [
                      /* @__PURE__ */ jsxRuntimeExports.jsx(
                        "span",
                        {
                          className: "dot",
                          style: {
                            background: statusColor,
                            animation: session.launching || running ? "pulse 1.6s infinite" : void 0
                          }
                        }
                      ),
                      /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-cap truncate", style: { minWidth: 0, flex: 1 }, children: session.status })
                    ]
                  }
                ),
                /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { display: "flex", gap: 8, alignItems: "stretch" }, children: /* @__PURE__ */ jsxRuntimeExports.jsxs(
                  "button",
                  {
                    className: "btn btn-primary",
                    style: {
                      flex: 1,
                      height: 62,
                      borderRadius: "var(--r-control)",
                      flexDirection: "column",
                      gap: 1,
                      display: "flex",
                      fontSize: 16,
                      fontWeight: 700,
                      boxShadow: running || session.launching ? "none" : "0 4px 22px rgba(255,211,34,0.28)"
                    },
                    disabled: !canLaunch,
                    onClick: () => {
                      if (selected === void 0 || account === void 0) return;
                      void session.launch({
                        versionId: selected,
                        playerName: account.name,
                        accountUuid: account.uuid,
                        loginMode: MODE_LABEL[mode]
                      });
                    },
                    children: [
                      /* @__PURE__ */ jsxRuntimeExports.jsxs("span", { style: { display: "flex", alignItems: "center", gap: 8 }, children: [
                        /* @__PURE__ */ jsxRuntimeExports.jsx(IconPlay, { size: 15 }),
                        " ",
                        session.launching ? "启动中…" : running ? "游戏运行中" : "启动游戏"
                      ] }),
                      /* @__PURE__ */ jsxRuntimeExports.jsx("span", { style: { fontSize: 11, fontWeight: 400, opacity: 0.72 }, children: selected ?? "未选择版本" })
                    ]
                  }
                ) })
              ]
            }
          )
        }
      )
    ] }),
    menu.node,
    host
  ] });
}
const KIND_LABEL = {
  mod: "模组",
  resourcepack: "资源包",
  shader: "光影"
};
const KIND_ORDER = ["mod", "resourcepack", "shader"];
const KIND_OPTIONS = KIND_ORDER.map((k2) => ({ value: k2, label: KIND_LABEL[k2] }));
function fmtSize$2(bytes) {
  if (!bytes) return "大小未知";
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}
function fmtTime$1(iso) {
  if (iso === void 0 || iso === null || iso === "") return "未记录";
  const d = new Date(iso);
  if (isNaN(d.getTime())) return "未记录";
  const p2 = (n2) => n2.toString().padStart(2, "0");
  return `${d.getFullYear()}-${p2(d.getMonth() + 1)}-${p2(d.getDate())} ${p2(d.getHours())}:${p2(d.getMinutes())}`;
}
function VersionDetailPage(props) {
  const { push, host } = useToasts();
  const [detail, setDetail] = reactExports.useState(null);
  const [loading, setLoading] = reactExports.useState(true);
  const [busy, setBusy] = reactExports.useState(null);
  const [kind, setKind] = reactExports.useState("mod");
  const [confirmDelete, setConfirmDelete] = reactExports.useState(null);
  const [showSettings, setShowSettings] = reactExports.useState(false);
  const [updates, setUpdates] = reactExports.useState(null);
  const [deps, setDeps] = reactExports.useState(null);
  const [installOpen, setInstallOpen] = reactExports.useState(false);
  const [progress, setProgress] = reactExports.useState("");
  const load = reactExports.useCallback(async () => {
    setLoading(true);
    try {
      setDetail(await request("version.detail", { versionId: props.versionId }));
    } catch (e) {
      push("error", `读取版本详情失败：${String(e)}`);
    } finally {
      setLoading(false);
    }
  }, [props.versionId]);
  reactExports.useEffect(() => {
    void load();
  }, [load]);
  reactExports.useEffect(() => {
    let alive = true;
    onBridgeEvent((event, params) => {
      if (!alive || event !== "content.progress") return;
      const stage = String(params.stage ?? "");
      if (stage !== "") setProgress(stage);
    });
    return () => {
      alive = false;
    };
  }, []);
  const filesFor = (d, k2) => {
    if (d === null) return [];
    return k2 === "mod" ? d.mods : k2 === "resourcepack" ? d.resourcepacks : d.shaderpacks;
  };
  const replaceFiles = (files2) => {
    setDetail((prev) => {
      if (prev === null) return prev;
      const k2 = kind;
      if (k2 === "mod") return { ...prev, mods: files2 };
      if (k2 === "resourcepack") return { ...prev, resourcepacks: files2 };
      return { ...prev, shaderpacks: files2 };
    });
  };
  async function toggle(file) {
    setBusy(file.fileName);
    try {
      const r2 = await request("version.toggleContent", {
        versionId: props.versionId,
        kind,
        fileName: file.fileName,
        enabled: !file.enabled
      });
      replaceFiles(r2.files);
    } catch (e) {
      push("error", String(e));
    } finally {
      setBusy(null);
    }
  }
  async function remove(file) {
    setBusy(file.fileName);
    try {
      const r2 = await request("version.deleteContent", {
        versionId: props.versionId,
        kind,
        fileName: file.fileName
      });
      replaceFiles(r2.files);
      push("success", `已删除 ${file.displayName}`);
    } catch (e) {
      push("error", String(e));
    } finally {
      setBusy(null);
    }
  }
  async function openFolder() {
    try {
      await request("version.openContentFolder", { versionId: props.versionId, kind });
    } catch (e) {
      push("error", String(e));
    }
  }
  async function checkUpdates() {
    setBusy("__updates__");
    try {
      const r2 = await request("version.checkUpdates", {
        versionId: props.versionId
      });
      setUpdates(r2.updates);
    } catch (e) {
      push("error", `检查更新失败：${String(e)}`);
    } finally {
      setBusy(null);
    }
  }
  async function checkDependencies() {
    setBusy("__deps__");
    try {
      setDeps(await request("version.checkDependencies", { versionId: props.versionId }));
    } catch (e) {
      push("error", `依赖检查失败：${String(e)}`);
    } finally {
      setBusy(null);
    }
  }
  async function updateOne(u2) {
    setBusy(u2.fileName);
    try {
      await request("version.updateMod", { versionId: props.versionId, fileName: u2.fileName });
      push("success", `${u2.fileName} → ${u2.latestVersion}`);
      await load();
    } catch (e) {
      push("error", String(e));
    } finally {
      setBusy(null);
    }
  }
  const files = filesFor(detail, kind);
  const globalBusy = busy !== null;
  return /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
    /* @__PURE__ */ jsxRuntimeExports.jsxs(
      PageShell,
      {
        title: `版本详情 · ${props.versionId}`,
        subtitle: detail === null ? "实例内容与参数" : `MC ${detail.mcVersion} · ${detail.loader}` + (detail.isolated ? " · 独立目录" : " · 共享目录"),
        actions: /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
          /* @__PURE__ */ jsxRuntimeExports.jsxs(
            "button",
            {
              className: "btn btn-subtle",
              disabled: detail === null,
              onClick: () => setShowSettings(true),
              children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(IconSettings, { size: 14 }),
                " 实例设置"
              ]
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsxs(
            "button",
            {
              className: "btn btn-subtle",
              disabled: detail === null,
              onClick: () => void openFolder(),
              children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(IconFolder, { size: 14 }),
                " 打开目录"
              ]
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsxs("button", { className: "btn btn-subtle", onClick: () => void load(), children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx(IconRefresh, { size: 14 }),
            " 刷新"
          ] })
        ] }),
        children: [
          loading ? /* @__PURE__ */ jsxRuntimeExports.jsx(SkeletonList, { rows: 6 }) : /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
            detail !== null && /* @__PURE__ */ jsxRuntimeExports.jsx(
              "div",
              {
                className: "card glow",
                style: {
                  padding: "18px 20px",
                  marginBottom: 12,
                  "--glow": "radial-gradient(520px circle at 20% 0%, color-mix(in srgb, var(--accent) 14%, transparent), transparent 68%)"
                },
                children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 26, flexWrap: "wrap" }, children: [
                  /* @__PURE__ */ jsxRuntimeExports.jsx(Info, { label: "游戏版本", value: detail.mcVersion }),
                  /* @__PURE__ */ jsxRuntimeExports.jsx(
                    Info,
                    {
                      label: "加载器",
                      value: detail.loaderVersion != null ? `${detail.loader} ${detail.loaderVersion}` : detail.loader
                    }
                  ),
                  /* @__PURE__ */ jsxRuntimeExports.jsx(Info, { label: "隔离", value: detail.isolated ? "独立版本目录" : "共享 .minecraft" }),
                  /* @__PURE__ */ jsxRuntimeExports.jsx(Info, { label: "最近游玩", value: fmtTime$1(detail.lastPlayedAt) }),
                  /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { minWidth: 260 }, children: [
                    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-meta", children: "实例目录" }),
                    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-body truncate", children: detail.gameRoot })
                  ] })
                ] })
              }
            ),
            /* @__PURE__ */ jsxRuntimeExports.jsxs(
              "div",
              {
                style: {
                  display: "flex",
                  alignItems: "center",
                  gap: 10,
                  flexWrap: "wrap",
                  marginBottom: 12
                },
                children: [
                  /* @__PURE__ */ jsxRuntimeExports.jsx(
                    Segmented,
                    {
                      value: kind,
                      options: KIND_OPTIONS,
                      onChange: setKind
                    }
                  ),
                  /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { flex: 1 } }),
                  kind === "mod" && /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
                    /* @__PURE__ */ jsxRuntimeExports.jsxs(
                      "button",
                      {
                        className: "btn",
                        disabled: globalBusy,
                        onClick: () => void checkUpdates(),
                        children: [
                          /* @__PURE__ */ jsxRuntimeExports.jsx(IconRefresh, { size: 14 }),
                          " 检查更新"
                        ]
                      }
                    ),
                    /* @__PURE__ */ jsxRuntimeExports.jsx(
                      "button",
                      {
                        className: "btn",
                        disabled: globalBusy,
                        onClick: () => void checkDependencies(),
                        children: "⚖ 检查依赖"
                      }
                    )
                  ] }),
                  /* @__PURE__ */ jsxRuntimeExports.jsx(
                    "button",
                    {
                      className: "btn btn-primary",
                      disabled: globalBusy,
                      onClick: () => setInstallOpen(true),
                      children: "↓ 安装内容"
                    }
                  )
                ]
              }
            ),
            progress !== "" && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "info info-info", style: { marginBottom: 12 }, children: [
              /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "spinner", "aria-hidden": true }),
              /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "info-body t-body", children: progress })
            ] }),
            files.length === 0 ? /* @__PURE__ */ jsxRuntimeExports.jsx(
              EmptyState,
              {
                title: `还没有${KIND_LABEL[kind]}`,
                hint: "点「安装内容」从 Modrinth 搜索安装。"
              }
            ) : files.map((file, index) => /* @__PURE__ */ jsxRuntimeExports.jsxs(
              "div",
              {
                className: "card glow",
                style: {
                  display: "flex",
                  alignItems: "center",
                  gap: 12,
                  padding: "10px 14px",
                  marginBottom: 8,
                  animation: `rowIn var(--t-enter) var(--e-enter) both`,
                  animationDelay: `${index * 39}ms`,
                  "--glow": "radial-gradient(420px circle at 12% 50%, color-mix(in srgb, var(--accent) 14%, transparent), transparent 66%)"
                },
                children: [
                  /* @__PURE__ */ jsxRuntimeExports.jsx("span", { style: { fontSize: 15, opacity: file.enabled ? 1 : 0.45 }, children: file.enabled ? "☑" : "☐" }),
                  /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minWidth: 0 }, children: [
                    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-strong truncate", children: file.displayName }),
                    /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-cap", children: [
                      fmtSize$2(file.sizeBytes),
                      file.enabled ? "" : " · 已停用"
                    ] })
                  ] }),
                  /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", disabled: globalBusy, onClick: () => void toggle(file), children: file.enabled ? "停用" : "启用" }),
                  /* @__PURE__ */ jsxRuntimeExports.jsxs(
                    "button",
                    {
                      className: "btn btn-subtle btn-danger",
                      disabled: globalBusy,
                      onClick: () => setConfirmDelete(file),
                      children: [
                        /* @__PURE__ */ jsxRuntimeExports.jsx(IconClose, { size: 14 }),
                        " 删除"
                      ]
                    }
                  )
                ]
              },
              file.fileName
            ))
          ] }),
          host
        ]
      }
    ),
    /* @__PURE__ */ jsxRuntimeExports.jsx(
      Modal,
      {
        open: confirmDelete !== null,
        title: `删除 ${confirmDelete?.displayName}？`,
        width: 460,
        onClose: () => setConfirmDelete(null),
        footer: /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: () => setConfirmDelete(null), children: "取消" }),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn btn-primary",
              style: { background: "var(--danger)", color: "#fff" },
              onClick: () => {
                const t2 = confirmDelete;
                setConfirmDelete(null);
                if (t2 !== null) void remove(t2);
              },
              children: "删除"
            }
          )
        ] }),
        children: /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-body", children: "文件会从版本目录永久删除，不可恢复。" })
      }
    ),
    updates !== null && /* @__PURE__ */ jsxRuntimeExports.jsx(
      UpdatesDialog,
      {
        updates,
        busy,
        onUpdate: updateOne,
        onClose: () => {
          setUpdates(null);
          void load();
        }
      }
    ),
    deps !== null && /* @__PURE__ */ jsxRuntimeExports.jsx(DepsDialog, { deps, versionId: props.versionId, onClose: () => setDeps(null) }),
    showSettings && detail !== null && /* @__PURE__ */ jsxRuntimeExports.jsx(
      InstanceSettingsDialog,
      {
        versionId: props.versionId,
        onClose: () => setShowSettings(false),
        onSaved: () => {
          setShowSettings(false);
          void load();
        }
      }
    ),
    installOpen && detail !== null && /* @__PURE__ */ jsxRuntimeExports.jsx(
      SearchInstallDialog,
      {
        kind,
        mcVersion: detail.mcVersion,
        loader: detail.loader,
        versionId: props.versionId,
        onInstalled: () => {
          void load();
        },
        onClose: () => setInstallOpen(false)
      }
    )
  ] });
}
function Info(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { minWidth: 150 }, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-meta", children: props.label }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-body", children: props.value })
  ] });
}
function UpdatesDialog(props) {
  const [done, setDone] = reactExports.useState([]);
  const updateAll = async () => {
    for (const u2 of props.updates) {
      if (done.includes(u2.fileName)) continue;
      await props.onUpdate(u2);
      setDone((prev) => [...prev, u2.fileName]);
    }
  };
  return /* @__PURE__ */ jsxRuntimeExports.jsx(
    Modal,
    {
      open: true,
      title: `模组更新（${props.updates.length}）`,
      width: 600,
      onClose: props.onClose,
      footer: /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: props.onClose, children: "关闭" }),
        /* @__PURE__ */ jsxRuntimeExports.jsx(
          "button",
          {
            className: "btn btn-primary",
            disabled: props.busy !== null,
            onClick: () => void updateAll(),
            children: "全部更新"
          }
        )
      ] }),
      children: /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { maxHeight: 380, overflowY: "auto", minWidth: 0 }, children: props.updates.map((u2) => {
        const isDone = done.includes(u2.fileName);
        return /* @__PURE__ */ jsxRuntimeExports.jsxs(
          "div",
          {
            className: "row row-static",
            style: { padding: "8px 0", borderRadius: 0 },
            children: [
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minWidth: 0 }, children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-strong truncate", children: u2.fileName }),
                /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-cap", children: [
                  u2.currentVersion,
                  " → ",
                  u2.latestVersion
                ] })
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsx(
                "button",
                {
                  className: "btn",
                  disabled: isDone || props.busy !== null,
                  onClick: () => {
                    setDone((prev) => [...prev, u2.fileName]);
                    void props.onUpdate(u2);
                  },
                  children: isDone ? "已更新" : "更新"
                }
              )
            ]
          },
          u2.fileName
        );
      }) })
    }
  );
}
function DepsDialog(props) {
  const [stage, setStage] = reactExports.useState("");
  const [busy, setBusy] = reactExports.useState(false);
  const [installed, setInstalled] = reactExports.useState(null);
  const clean = props.deps.missing.length === 0 && props.deps.incompatible.length === 0;
  const installMissing = async () => {
    setBusy(true);
    try {
      for (const d of props.deps.missing) {
        setStage(`安装 ${d.title}…`);
        await request("content.install", {
          kind: "mod",
          projectId: d.projectId,
          title: d.title,
          targetVersionId: props.versionId
        });
      }
      setInstalled(props.deps.missing.length);
      setStage("");
    } finally {
      setBusy(false);
    }
  };
  return /* @__PURE__ */ jsxRuntimeExports.jsx(
    Modal,
    {
      open: true,
      title: "依赖检查",
      width: 600,
      onClose: props.onClose,
      footer: /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: props.onClose, children: "关闭" }),
        props.deps.missing.length > 0 && /* @__PURE__ */ jsxRuntimeExports.jsx(
          "button",
          {
            className: "btn btn-primary",
            disabled: busy,
            onClick: () => void installMissing(),
            children: busy ? "安装中…" : "安装缺失依赖"
          }
        )
      ] }),
      children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { maxHeight: 380, overflowY: "auto", minWidth: 0 }, children: [
        clean && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "info info-success", children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx(IconCheck, { size: 14 }),
          /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "info-body", children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "info-title", children: "依赖完整" }),
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: "没有缺失的必需依赖，也没有已安装的不兼容模组。" })
          ] })
        ] }),
        props.deps.missing.length > 0 && /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
          /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-strong", style: { display: "block", marginTop: 8 }, children: [
            "缺失的必需依赖（",
            props.deps.missing.length,
            "）"
          ] }),
          props.deps.missing.map((d) => /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "card", style: { padding: "8px 12px", marginTop: 6 }, children: /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-body", children: d.title }) }, d.projectId))
        ] }),
        props.deps.incompatible.length > 0 && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "info info-danger", style: { marginTop: 10 }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx(IconClose, { size: 14 }),
          /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "info-body", children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "info-title", children: "已安装但不兼容" }),
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: props.deps.incompatible.join("、") })
          ] })
        ] }),
        props.deps.optional.length > 0 && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-cap", style: { marginTop: 8 }, children: [
          "可选依赖 ",
          props.deps.optional.length,
          " 个（未安装不影响启动）"
        ] }),
        props.deps.truncated && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", style: { marginTop: 6 }, children: "模组数量超过 200，只检查了前 200 个。" }),
        stage !== "" && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-body", style: { marginTop: 8, color: "var(--accent)" }, children: stage }),
        installed !== null && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-body", style: { marginTop: 8, color: "var(--success)" }, children: [
          "已安装 ",
          installed,
          " 个依赖"
        ] })
      ] })
    }
  );
}
function InstanceSettingsDialog(props) {
  const [initial, setInitial] = reactExports.useState(null);
  const [isolation, setIsolation] = reactExports.useState("global");
  const [memoryMode, setMemoryMode] = reactExports.useState("global");
  const [minMem, setMinMem] = reactExports.useState("512");
  const [maxMem, setMaxMem] = reactExports.useState("4096");
  const [javaMode, setJavaMode] = reactExports.useState("global");
  const [javaPath, setJavaPath] = reactExports.useState("");
  const [windowMode, setWindowMode] = reactExports.useState("global");
  const [width, setWidth] = reactExports.useState("854");
  const [height, setHeight] = reactExports.useState("480");
  const [fullscreen, setFullscreen] = reactExports.useState(false);
  const [busy, setBusy] = reactExports.useState(false);
  const [error, setError] = reactExports.useState("");
  reactExports.useEffect(() => {
    void (async () => {
      try {
        const s = await request("version.settings", { versionId: props.versionId });
        setInitial(s);
        setIsolation(s.isolation ?? "global");
        setMemoryMode(s.memoryMode);
        setMinMem(`${s.memoryMinMb ?? s.effective.minMem}`);
        setMaxMem(`${s.memoryMaxMb ?? s.effective.maxMem}`);
        setJavaMode(s.javaMode);
        setJavaPath(s.javaPath ?? "");
        setWindowMode(s.windowMode);
        setWidth(`${s.windowWidth ?? s.effective.windowW ?? 854}`);
        setHeight(`${s.windowHeight ?? s.effective.windowH ?? 480}`);
        setFullscreen(s.fullscreen ?? s.effective.fullscreen ?? false);
      } catch (e) {
        setError(String(e));
      }
    })();
  }, [props.versionId]);
  const save = async () => {
    setBusy(true);
    setError("");
    try {
      await request("version.saveSettings", {
        versionId: props.versionId,
        isolation: isolation === "global" ? null : isolation,
        memoryMode,
        memoryMinMb: memoryMode === "manual" ? Number(minMem) : null,
        memoryMaxMb: memoryMode === "manual" ? Number(maxMem) : null,
        javaMode,
        javaPath: javaMode === "manual" ? javaPath : null,
        windowMode,
        windowWidth: windowMode === "manual" ? Number(width) : null,
        windowHeight: windowMode === "manual" ? Number(height) : null,
        fullscreen: windowMode === "manual" ? fullscreen : null
      });
      props.onSaved();
    } catch (e) {
      setError(String(e));
    } finally {
      setBusy(false);
    }
  };
  const eff = initial?.effective;
  return /* @__PURE__ */ jsxRuntimeExports.jsx(
    Modal,
    {
      open: true,
      title: `实例设置 · ${props.versionId}`,
      width: 600,
      onClose: props.onClose,
      footer: /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: props.onClose, children: "取消" }),
        /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn btn-primary", disabled: busy, onClick: () => void save(), children: busy ? "保存中…" : "保存" })
      ] }),
      children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { maxHeight: 460, overflowY: "auto", minWidth: 0 }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx(
          LabeledSelect,
          {
            label: "版本隔离",
            value: isolation,
            options: [
              ["global", "跟随全局"],
              ["shared", "共享 .minecraft"],
              ["isolated", "独立版本目录"]
            ],
            onChange: setIsolation
          }
        ),
        /* @__PURE__ */ jsxRuntimeExports.jsx(
          LabeledSelect,
          {
            label: "内存档位",
            value: memoryMode,
            options: [
              ["global", "跟随全局"],
              ["manual", "自定义"],
              ["auto", "自动推荐"]
            ],
            onChange: setMemoryMode
          }
        ),
        memoryMode === "manual" && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 10, marginTop: 8 }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx(LabeledInput, { label: "最小内存（MB）", value: minMem, onChange: setMinMem }),
          /* @__PURE__ */ jsxRuntimeExports.jsx(LabeledInput, { label: "最大内存（MB）", value: maxMem, onChange: setMaxMem })
        ] }),
        /* @__PURE__ */ jsxRuntimeExports.jsx(
          LabeledSelect,
          {
            label: "Java 档位",
            value: javaMode,
            options: [
              ["global", "跟随全局"],
              ["manual", "自定义路径"],
              ["auto", "按版本 JSON 推荐"]
            ],
            onChange: setJavaMode
          }
        ),
        javaMode === "manual" && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { marginTop: 8 }, children: /* @__PURE__ */ jsxRuntimeExports.jsx(LabeledInput, { label: "java 可执行文件路径", value: javaPath, onChange: setJavaPath }) }),
        /* @__PURE__ */ jsxRuntimeExports.jsx(
          LabeledSelect,
          {
            label: "窗口档位",
            value: windowMode,
            options: [
              ["global", "跟随全局"],
              ["manual", "自定义"],
              ["auto", "854×480 窗口化"]
            ],
            onChange: setWindowMode
          }
        ),
        windowMode === "manual" && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 10, marginTop: 8, alignItems: "flex-end" }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx(LabeledInput, { label: "宽", value: width, onChange: setWidth }),
          /* @__PURE__ */ jsxRuntimeExports.jsx(LabeledInput, { label: "高", value: height, onChange: setHeight }),
          /* @__PURE__ */ jsxRuntimeExports.jsxs("label", { style: { display: "flex", alignItems: "center", gap: 6, fontSize: 12 }, children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx(
              "input",
              {
                type: "checkbox",
                checked: fullscreen,
                onChange: (e) => setFullscreen(e.target.checked)
              }
            ),
            "全屏"
          ] })
        ] }),
        eff !== void 0 && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-cap", style: { opacity: 0.7, marginTop: 14 }, children: [
          "当前生效：内存 ",
          eff.minMem,
          "–",
          eff.maxMem,
          " MB · Java ",
          eff.java ?? "自动探测",
          " · 窗口",
          " ",
          eff.windowW ?? 854,
          "×",
          eff.windowH ?? 480,
          eff.fullscreen === true ? "（全屏）" : ""
        ] }),
        error !== "" && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "info info-danger", style: { marginTop: 10 }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx(IconClose, { size: 14 }),
          /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "info-body t-body", children: error })
        ] })
      ] })
    }
  );
}
function LabeledSelect(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { marginTop: 12 }, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-meta", style: { display: "block", marginBottom: 4 }, children: props.label }),
    /* @__PURE__ */ jsxRuntimeExports.jsx(
      Select,
      {
        width: 520,
        value: props.value,
        options: props.options.map(([v2, label]) => ({ value: v2, label })),
        onChange: props.onChange
      }
    )
  ] });
}
function LabeledInput(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1 }, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-meta", style: { display: "block", marginBottom: 4 }, children: props.label }),
    /* @__PURE__ */ jsxRuntimeExports.jsx(
      "input",
      {
        className: "input",
        style: { width: "100%" },
        value: props.value,
        onChange: (e) => props.onChange(e.target.value)
      }
    )
  ] });
}
function SearchInstallDialog(props) {
  const [query, setQuery] = reactExports.useState("");
  const [hits, setHits] = reactExports.useState([]);
  const [searching, setSearching] = reactExports.useState(false);
  const [installing, setInstalling] = reactExports.useState(null);
  const [stage, setStage] = reactExports.useState("");
  const [installed, setInstalled] = reactExports.useState(null);
  reactExports.useEffect(() => {
    let alive = true;
    onBridgeEvent((event, params) => {
      if (!alive || event !== "content.progress") return;
      const s = String(params.stage ?? "");
      if (s !== "") setStage(s);
    });
    return () => {
      alive = false;
    };
  }, []);
  const search = async () => {
    setSearching(true);
    try {
      const r2 = await request("content.search", {
        kind: props.kind,
        query,
        gameVersion: props.mcVersion,
        loader: props.kind === "mod" ? props.loader : void 0
      });
      setHits(r2.hits);
    } finally {
      setSearching(false);
    }
  };
  const install = async (hit) => {
    setInstalling(hit.projectId);
    setStage("");
    try {
      const r2 = await request("content.install", {
        kind: props.kind,
        projectId: hit.projectId,
        title: hit.title,
        targetVersionId: props.versionId
      });
      setInstalled(r2);
      props.onInstalled();
    } finally {
      setInstalling(null);
      setStage("");
    }
  };
  return /* @__PURE__ */ jsxRuntimeExports.jsx(
    Modal,
    {
      open: true,
      title: `安装${KIND_LABEL[props.kind]}（Modrinth）`,
      width: 720,
      onClose: props.onClose,
      footer: /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: props.onClose, children: installed !== null ? "完成" : "关闭" }),
      children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { minWidth: 0 }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 10, marginBottom: 12 }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "input",
            {
              className: "input",
              style: { flex: 1 },
              value: query,
              placeholder: `搜索${KIND_LABEL[props.kind]}（留空 = 热门）`,
              onChange: (e) => setQuery(e.target.value),
              onKeyDown: (e) => {
                if (e.key === "Enter") void search();
              }
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn btn-primary", disabled: searching, onClick: () => void search(), children: searching ? "搜索中…" : "搜索" })
        ] }),
        stage !== "" && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-body", style: { display: "block", marginBottom: 8, color: "var(--accent)" }, children: stage }),
        installed !== null && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "info info-success", style: { marginBottom: 10 }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx(IconCheck, { size: 14 }),
          /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "info-body t-body", children: [
            "已安装 ",
            installed.installed.length,
            " 个文件",
            installed.missing.length > 0 ? `；${installed.missing.length} 个依赖未找到` : ""
          ] })
        ] }),
        /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { maxHeight: 360, overflowY: "auto" }, children: hits.length === 0 ? /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", style: { textAlign: "center", padding: 30 }, children: searching ? "正在搜索…" : "输入关键词后点「搜索」" }) : hits.map((hit, index) => /* @__PURE__ */ jsxRuntimeExports.jsxs(
          "div",
          {
            style: {
              display: "flex",
              gap: 12,
              alignItems: "center",
              padding: "10px 0",
              borderBottom: "1px solid var(--stroke-divider)",
              animation: `rowIn var(--t-enter) var(--e-enter) both`,
              animationDelay: `${index * 39}ms`
            },
            children: [
              /* @__PURE__ */ jsxRuntimeExports.jsx(
                "div",
                {
                  style: {
                    width: 40,
                    height: 40,
                    borderRadius: "var(--r-control)",
                    overflow: "hidden",
                    background: "var(--surface-hover)",
                    display: "flex",
                    alignItems: "center",
                    justifyContent: "center",
                    flexShrink: 0
                  },
                  children: hit.iconUrl != null && hit.iconUrl !== "" ? /* @__PURE__ */ jsxRuntimeExports.jsx(
                    "img",
                    {
                      src: hit.iconUrl,
                      alt: "",
                      style: { width: "100%", height: "100%", objectFit: "cover" }
                    }
                  ) : /* @__PURE__ */ jsxRuntimeExports.jsx(IconCube, { size: 20, style: { opacity: 0.6 } })
                }
              ),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minWidth: 0 }, children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-strong truncate", children: hit.title }),
                /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-cap", children: [
                  hit.author,
                  " · ",
                  hit.downloads.toLocaleString(),
                  " 次下载"
                ] }),
                /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-meta truncate", children: hit.description })
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsx(
                "button",
                {
                  className: "btn",
                  disabled: installing !== null,
                  onClick: () => void install(hit),
                  children: installing === hit.projectId ? "安装中…" : "安装"
                }
              )
            ]
          },
          hit.projectId
        )) })
      ] })
    }
  );
}
const LOADER_ICONS = {
  neoforge: neoforgeIcon,
  forge: forgeIcon,
  fabric: fabricIcon,
  quilt: quiltIcon
};
const TYPE_ICONS = {
  release: grassIcon,
  snapshot: amethystIcon,
  old_beta: cobbleIcon,
  old_alpha: dirtIcon
};
const LOADER_LABEL = {
  neoforge: "NeoForge",
  forge: "Forge",
  fabric: "Fabric",
  quilt: "Quilt"
};
const TYPE_LABEL$2 = {
  release: "正式版",
  snapshot: "快照版",
  old_beta: "远古 Beta",
  old_alpha: "远古 Alpha"
};
function iconFor$1(v2) {
  const loader = v2.loader ?? "";
  if (LOADER_ICONS[loader] !== void 0) return LOADER_ICONS[loader];
  const lower = v2.id.toLowerCase();
  if (lower.includes("neoforge")) return neoforgeIcon;
  if (lower.includes("forge")) return forgeIcon;
  if (lower.includes("fabric")) return fabricIcon;
  if (lower.includes("quilt")) return quiltIcon;
  return TYPE_ICONS[v2.type ?? "release"] ?? grassIcon;
}
function tagFor(v2) {
  const loader = v2.loader ?? "";
  if (LOADER_LABEL[loader] !== void 0) return LOADER_LABEL[loader];
  const lower = v2.id.toLowerCase();
  if (lower.includes("neoforge")) return "NeoForge";
  if (lower.includes("forge")) return "Forge";
  if (lower.includes("fabric")) return "Fabric";
  if (lower.includes("quilt")) return "Quilt";
  return TYPE_LABEL$2[v2.type ?? "release"] ?? "正式版";
}
function fmtSize$1(bytes) {
  if (!bytes) return "未完成";
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}
function lastPlayed(iso) {
  if (iso === void 0 || iso === null || iso === "") return "从未启动";
  const d = new Date(iso);
  if (isNaN(d.getTime())) return "从未启动";
  const pad = (n2) => n2.toString().padStart(2, "0");
  const hm = `${pad(d.getHours())}:${pad(d.getMinutes())}`;
  const today = /* @__PURE__ */ new Date();
  const dayDiff = Math.floor(
    (new Date(today.getFullYear(), today.getMonth(), today.getDate()).getTime() - new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime()) / 864e5
  );
  if (dayDiff <= 0) return `今天 ${hm}`;
  if (dayDiff === 1) return `昨天 ${hm}`;
  if (dayDiff < 30) return `${dayDiff} 天前`;
  if (dayDiff < 365) return `${Math.floor(dayDiff / 30)} 个月前`;
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}
function sortVersions(list) {
  return [...list].sort((a, b) => {
    const aLast = a.isDefault === true ? 1 : 0;
    const bLast = b.isDefault === true ? 1 : 0;
    if (aLast !== bLast) return bLast - aLast;
    const ai2 = a.id.toLowerCase();
    const bi2 = b.id.toLowerCase();
    return ai2 < bi2 ? 1 : ai2 > bi2 ? -1 : 0;
  });
}
function VersionsPage(props) {
  const { push, host } = useToasts();
  const [items, setItems] = reactExports.useState([]);
  const [loading, setLoading] = reactExports.useState(true);
  const [busy, setBusy] = reactExports.useState(null);
  const [confirm, setConfirm] = reactExports.useState(null);
  const [gameDir, setGameDir] = reactExports.useState("");
  const [query, setQuery] = reactExports.useState("");
  const [kindFilter, setKindFilter] = reactExports.useState("全部");
  const [menuFor, setMenuFor] = reactExports.useState(null);
  const [openId, setOpenId] = reactExports.useState(null);
  async function reload() {
    setLoading(true);
    try {
      const r2 = await request("catalog.installed");
      setItems(sortVersions(r2.installed));
    } catch (e) {
      push("error", `读取版本列表失败：${String(e)}`);
    } finally {
      setLoading(false);
    }
    try {
      const s = await request("settings.get");
      setGameDir(s.gameDir);
    } catch {
    }
  }
  reactExports.useEffect(() => {
    void reload();
  }, []);
  const kinds = ["全部", "正式版", "快照版", "NeoForge", "Forge", "Fabric", "Quilt"];
  const filtered = reactExports.useMemo(() => {
    const q2 = query.trim().toLowerCase();
    return items.filter((v2) => {
      if (q2 !== "" && !v2.id.toLowerCase().includes(q2)) return false;
      if (kindFilter !== "全部" && tagFor(v2) !== kindFilter) return false;
      return true;
    });
  }, [items, query, kindFilter]);
  async function openFolder() {
    try {
      await request("versions.openFolder", {});
    } catch (e) {
      push("error", String(e));
    }
  }
  async function openVersionFolder(v2) {
    setBusy(v2.id);
    try {
      await request("versions.openFolder", { versionId: v2.id });
    } catch (e) {
      push("error", String(e));
    } finally {
      setBusy(null);
    }
  }
  async function setDefault(v2) {
    setBusy(v2.id);
    try {
      await request("versions.setDefault", { versionId: v2.id });
      push("success", `已将 ${v2.id} 设为默认版本`);
      await reload();
    } catch (e) {
      push("error", String(e));
    } finally {
      setBusy(null);
    }
  }
  async function remove(v2) {
    setBusy(v2.id);
    try {
      const r2 = await request("versions.delete", { versionId: v2.id });
      push("success", `已删除 ${v2.id}（释放 ${fmtSize$1(r2.freedBytes)}）`);
      await reload();
    } catch (e) {
      push("error", String(e));
    } finally {
      setBusy(null);
    }
  }
  if (openId !== null) {
    return /* @__PURE__ */ jsxRuntimeExports.jsx(VersionDetailPage, { versionId: openId, onBack: () => setOpenId(null) });
  }
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page", children: [
    /* @__PURE__ */ jsxRuntimeExports.jsxs(
      "div",
      {
        style: {
          display: "flex",
          gap: 8,
          alignItems: "center",
          marginBottom: 14,
          flexShrink: 0
        },
        children: [
          /* @__PURE__ */ jsxRuntimeExports.jsxs(
            "div",
            {
              className: "input",
              style: { flex: 1, minWidth: 0, display: "flex", alignItems: "center", gap: 8 },
              title: gameDir,
              children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(IconFolder, { size: 15, style: { opacity: 0.6 } }),
                /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "truncate", style: { fontSize: 12, color: "var(--text-secondary)" }, children: gameDir === "" ? "游戏目录" : gameDir })
              ]
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsxs(
            "button",
            {
              className: "btn",
              onClick: () => push("info", "整合包导入：内核能力接入后可用"),
              title: "导入整合包",
              children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(IconImport, { size: 15 }),
                " 导入整合包"
              ]
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn",
              onClick: () => push("info", "从链接导入：内核能力接入后可用"),
              title: "从链接导入",
              children: "从链接导入"
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "input",
            {
              className: "input",
              style: { width: 210 },
              placeholder: "搜索版本 / 类别",
              value: query,
              onChange: (e) => setQuery(e.target.value)
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            Select,
            {
              width: 110,
              value: kindFilter,
              options: kinds.map((k2) => ({ value: k2, label: k2 })),
              onChange: setKindFilter
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsxs("button", { className: "btn", onClick: () => void reload(), disabled: loading, children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx(IconRefresh, { size: 14 }),
            " 刷新"
          ] }),
          /* @__PURE__ */ jsxRuntimeExports.jsxs("button", { className: "btn", onClick: () => void openFolder(), children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx(IconFolder, { size: 15 }),
            " 打开游戏目录"
          ] })
        ]
      }
    ),
    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "card card-flush", style: { flex: 1, minHeight: 0 }, children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minHeight: 0, overflowY: "auto", overflowX: "hidden", padding: "6px 10px 10px" }, children: [
      loading && /* @__PURE__ */ jsxRuntimeExports.jsx(SkeletonList, { rows: 6, avatar: true }),
      !loading && filtered.length === 0 && /* @__PURE__ */ jsxRuntimeExports.jsx(
        EmptyState,
        {
          title: items.length === 0 ? "还没有已安装的版本" : "没有匹配的版本",
          hint: items.length === 0 ? "去「下载」页选择一个版本安装。" : "换个关键词或类别试试。"
        }
      ),
      filtered.map((v2) => /* @__PURE__ */ jsxRuntimeExports.jsxs(
        "div",
        {
          className: "row",
          style: {
            display: "grid",
            gridTemplateColumns: "auto minmax(120px, 260px) minmax(0, 1fr) auto auto auto",
            columnGap: 12
          },
          onClick: () => setOpenId(v2.id),
          children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "icon32", children: /* @__PURE__ */ jsxRuntimeExports.jsx("img", { src: iconFor$1(v2), alt: "", width: 32, height: 32 }) }),
            /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-strong truncate", children: v2.id }),
            /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 6, alignItems: "center" }, children: [
              /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "chip", children: tagFor(v2) }),
              v2.isolated === true && /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "chip", children: "版本隔离" }),
              v2.isDefault === true && /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "chip chip-accent", children: "默认" }),
              v2.jarExists === false && /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "chip chip-danger", children: "缺少 jar" })
            ] }),
            /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "meta", children: [
              /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "meta-k", children: "上次启动" }),
              /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "meta-v", children: lastPlayed(v2.lastPlayedAt) }),
              /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "meta-k", children: "大小" }),
              /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "meta-v", children: fmtSize$1(v2.sizeBytes) })
            ] }),
            /* @__PURE__ */ jsxRuntimeExports.jsx(
              "button",
              {
                className: "btn btn-subtle btn-sm",
                title: "更多",
                disabled: busy !== null,
                onClick: (e) => {
                  e.stopPropagation();
                  setMenuFor(v2);
                },
                children: /* @__PURE__ */ jsxRuntimeExports.jsx(IconMore, { size: 16 })
              }
            ),
            /* @__PURE__ */ jsxRuntimeExports.jsx(
              "button",
              {
                className: "btn btn-subtle btn-sm btn-danger",
                title: "删除",
                disabled: busy !== null,
                onClick: (e) => {
                  e.stopPropagation();
                  setConfirm(v2);
                },
                children: /* @__PURE__ */ jsxRuntimeExports.jsx(IconTrash, { size: 15 })
              }
            )
          ]
        },
        v2.id
      ))
    ] }) }),
    /* @__PURE__ */ jsxRuntimeExports.jsx(
      Modal,
      {
        open: menuFor !== null,
        title: menuFor?.id ?? "",
        onClose: () => setMenuFor(null),
        footer: /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: () => setMenuFor(null), children: "关闭" }),
        children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", flexDirection: "column", gap: 6 }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn",
              onClick: () => {
                const v2 = menuFor;
                setMenuFor(null);
                if (v2 !== null) void openVersionFolder(v2);
              },
              children: "打开版本目录"
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn",
              disabled: menuFor?.isDefault === true,
              onClick: () => {
                const v2 = menuFor;
                setMenuFor(null);
                if (v2 !== null) void setDefault(v2);
              },
              children: "设为默认版本"
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn",
              onClick: () => {
                const v2 = menuFor;
                setMenuFor(null);
                if (v2 !== null) setOpenId(v2.id);
              },
              children: "版本详情"
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: () => props.onOpenSettings(), children: "实例设置" })
        ] })
      }
    ),
    /* @__PURE__ */ jsxRuntimeExports.jsx(
      Modal,
      {
        open: confirm !== null,
        title: "删除版本？",
        onClose: () => setConfirm(null),
        footer: /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: () => setConfirm(null), children: "取消" }),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn btn-primary",
              style: { background: "var(--danger)", color: "#fff" },
              onClick: () => {
                const v2 = confirm;
                setConfirm(null);
                if (v2 !== null) void remove(v2);
              },
              children: "删除"
            }
          )
        ] }),
        children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "dialog-body", children: [
          "将删除版本 ",
          /* @__PURE__ */ jsxRuntimeExports.jsx("b", { children: confirm?.id }),
          " 的整个目录（含模组与配置），不可恢复。"
        ] })
      }
    ),
    host
  ] });
}
const CHANNEL_LABEL = {
  release: "正式版",
  snapshot: "快照版",
  ancient: "远古版本"
};
const TYPE_ICON = {
  release: grassIcon,
  snapshot: amethystIcon,
  old_beta: cobbleIcon,
  old_alpha: dirtIcon
};
const TYPE_LABEL$1 = {
  release: "正式版",
  snapshot: "快照版",
  old_beta: "远古 Beta",
  old_alpha: "远古 Alpha"
};
function iconFor(v2) {
  const t2 = v2.type.toLowerCase();
  if (t2 === "old_beta" || t2 === "old_alpha") return TYPE_ICON[t2];
  return TYPE_ICON[t2] ?? grassIcon;
}
function typeLabel(v2) {
  const t2 = v2.type.toLowerCase();
  return TYPE_LABEL$1[t2] ?? t2;
}
function releaseDate(iso) {
  if (!iso) return "—";
  const d = new Date(iso);
  if (isNaN(d.getTime())) return "—";
  const two = (n2) => n2.toString().padStart(2, "0");
  return `${d.getFullYear()}-${two(d.getMonth() + 1)}-${two(d.getDate())}`;
}
function releaseDateTime(iso) {
  if (!iso) return "—";
  const d = new Date(iso);
  if (isNaN(d.getTime())) return "—";
  const two = (n2) => n2.toString().padStart(2, "0");
  return `${releaseDate(iso)} ${two(d.getHours())}:${two(d.getMinutes())}`;
}
function channelOf(v2) {
  const t2 = v2.type.toLowerCase();
  if (t2 === "old_beta" || t2 === "old_alpha") return "ancient";
  return t2 === "snapshot" ? "snapshot" : "release";
}
function DownloadPage() {
  const { push, host } = useToasts();
  const [catalog, setCatalog] = reactExports.useState({ remote: [], installed: [] });
  const [loading, setLoading] = reactExports.useState(true);
  const [keyword, setKeyword] = reactExports.useState("");
  const [channel, setChannel] = reactExports.useState("release");
  const [progress, setProgress] = reactExports.useState(null);
  const [section, setSection] = reactExports.useState("vanilla");
  const installedIds = reactExports.useMemo(
    () => new Set(catalog.installed.map((i) => i.id)),
    [catalog.installed]
  );
  const load = reactExports.useCallback(async () => {
    setLoading(true);
    try {
      const data = await request("catalog.versions");
      setCatalog(data);
    } catch (e) {
      push("error", `版本清单拉取失败：${String(e)}`);
    }
    setLoading(false);
  }, [push]);
  reactExports.useEffect(() => {
    void load();
  }, [load]);
  reactExports.useEffect(() => {
    onBridgeEvent((event, params) => {
      if (event !== "catalog.versions.changed") return;
      const data = params;
      if (data && Array.isArray(data.remote)) setCatalog(data);
    });
  }, []);
  reactExports.useEffect(() => {
    let alive = true;
    onBridgeEvent((event, params) => {
      if (!alive) return;
      if (event === "install.progress") {
        setProgress({
          versionId: String(params.versionId ?? ""),
          stage: String(params.stage ?? ""),
          detail: String(params.detail ?? ""),
          current: Number(params.current ?? 0),
          total: Number(params.total ?? 0)
        });
      } else if (event === "install.done") {
        const id2 = String(params.versionId ?? "");
        setProgress(null);
        push("success", `${id2} 安装完成`);
        void load();
      } else if (event === "install.failed") {
        const id2 = String(params.versionId ?? "");
        setProgress(null);
        push("error", `${id2} 安装失败：${String(params.error ?? "未知错误")}`);
        void load();
      }
    });
    return () => {
      alive = false;
    };
  }, [push, load]);
  const installingId = progress?.versionId ?? "";
  const busy = progress !== null;
  async function install(versionId) {
    if (busy) return;
    setProgress({ versionId, stage: "准备中", detail: "", current: 0, total: 0 });
    try {
      await request("install.vanilla", { versionId });
    } catch (e) {
      setProgress(null);
      push("error", `安装请求被拒绝：${String(e)}`);
    }
  }
  const latestRelease = catalog.remote.find((v2) => v2.type.toLowerCase() === "release");
  const latestSnapshot = catalog.remote.find((v2) => v2.type.toLowerCase() === "snapshot");
  const filtered = reactExports.useMemo(() => {
    const q2 = keyword.trim().toLowerCase();
    return catalog.remote.filter((v2) => {
      if (channelOf(v2) !== channel) return false;
      if (q2 !== "" && !v2.id.toLowerCase().includes(q2)) return false;
      return true;
    });
  }, [catalog.remote, channel, keyword]);
  const subNav = [
    { id: "vanilla", label: "原版游戏", icon: /* @__PURE__ */ jsxRuntimeExports.jsx(IconHome, { size: 15 }) },
    { id: "mod", label: "Mod", icon: /* @__PURE__ */ jsxRuntimeExports.jsx(IconCube, { size: 15 }), group: "社区资源" },
    { id: "modpack", label: "整合包", icon: /* @__PURE__ */ jsxRuntimeExports.jsx(IconList, { size: 15 }) },
    { id: "datapack", label: "数据包", icon: /* @__PURE__ */ jsxRuntimeExports.jsx(IconList, { size: 15 }) },
    { id: "resourcepack", label: "资源包", icon: /* @__PURE__ */ jsxRuntimeExports.jsx(IconFolder, { size: 15 }) },
    { id: "shader", label: "光影包", icon: /* @__PURE__ */ jsxRuntimeExports.jsx(IconSparkle, { size: 15 }) }
  ];
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page", children: [
    /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minHeight: 0, display: "flex", gap: 16 }, children: [
      /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "card responsive-side", style: { width: 240, flexShrink: 0, padding: 8 }, children: subNav.map((item) => /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { children: [
        item.group !== void 0 && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", style: { padding: "8px 10px 4px" }, children: item.group }),
        /* @__PURE__ */ jsxRuntimeExports.jsxs(
          "button",
          {
            className: `nav-item${section === item.id ? " nav-active" : ""}`,
            style: { height: 38, width: "100%" },
            onClick: () => {
              setSection(item.id);
              if (item.id !== "vanilla") {
                push("info", `${item.label}：内容检索接入后可用`);
              }
            },
            children: [
              /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "nav-icon", style: { fontFamily: "inherit", fontSize: 14 }, children: item.icon }),
              /* @__PURE__ */ jsxRuntimeExports.jsx("span", { children: item.label })
            ]
          }
        )
      ] }, item.id)) }),
      /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minWidth: 0, display: "flex", flexDirection: "column" }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 10, alignItems: "center", marginBottom: 12 }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "seg responsive-seg", style: { width: 330, gridTemplateColumns: "repeat(3, 1fr)" }, children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx(
              "div",
              {
                className: "seg-ind",
                style: {
                  width: "calc(33.333% - 2px)",
                  // translateX 的百分比相对指示器自身宽度（= 一格）
                  transform: `translateX(calc(2px + ${(channel === "release" ? 0 : channel === "snapshot" ? 1 : 2) * 100}% + ${(channel === "release" ? 0 : channel === "snapshot" ? 1 : 2) * 2}px))`
                }
              }
            ),
            ["release", "snapshot", "ancient"].map((c) => /* @__PURE__ */ jsxRuntimeExports.jsx(
              "button",
              {
                className: `seg-item${channel === c ? " seg-active" : ""}`,
                onClick: () => setChannel(c),
                children: CHANNEL_LABEL[c]
              },
              c
            ))
          ] }),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "input",
            {
              className: "input",
              style: { flex: 1, minWidth: 0 },
              placeholder: "搜索版本 ID…",
              value: keyword,
              onChange: (e) => setKeyword(e.target.value)
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn btn-primary btn-sm", onClick: () => void load(), disabled: loading, children: "缓存" })
        ] }),
        !loading && (latestRelease !== void 0 || latestSnapshot !== void 0) && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { display: "flex", gap: 12, marginBottom: 12 }, children: [latestRelease, latestSnapshot].map(
          (v2, i) => v2 === void 0 ? null : /* @__PURE__ */ jsxRuntimeExports.jsxs(
            "div",
            {
              className: "card glow",
              style: { flex: 1, minWidth: 0, display: "flex", alignItems: "center", gap: 12, cursor: "pointer" },
              onClick: () => setKeyword(v2.id),
              children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "icon32", style: { width: 40, height: 40 }, children: /* @__PURE__ */ jsxRuntimeExports.jsx("img", { src: iconFor(v2), alt: "", width: 40, height: 40 }) }),
                /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minWidth: 0 }, children: [
                  /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: i === 0 ? "最新正式版" : "最新预览版" }),
                  /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-strong truncate", children: v2.id }),
                  /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-meta", children: [
                    "发布于 ",
                    releaseDateTime(v2.releaseTime)
                  ] })
                ] }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(IconChevronRight, { size: 15, style: { opacity: 0.5 } })
              ]
            },
            v2.id
          )
        ) }),
        progress !== null && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "info info-info", style: { marginBottom: 12 }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "spinner", "aria-hidden": true }),
          /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "info-body", children: [
            /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-body", children: [
              "正在安装 ",
              progress.versionId,
              " · ",
              progress.stage,
              progress.total > 0 && /* @__PURE__ */ jsxRuntimeExports.jsxs("span", { style: { opacity: 0.75 }, children: [
                " ",
                progress.current,
                "/",
                progress.total
              ] })
            ] }),
            progress.total > 0 && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { marginTop: 8 }, children: /* @__PURE__ */ jsxRuntimeExports.jsx(ProgressBar, { value: progress.current / progress.total * 100 }) })
          ] })
        ] }),
        /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "card card-flush", style: { flex: 1, minHeight: 0 }, children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minHeight: 0, overflowY: "auto", overflowX: "hidden", padding: "6px 10px 10px" }, children: [
          loading && /* @__PURE__ */ jsxRuntimeExports.jsx(SkeletonList, { rows: 7, avatar: true }),
          !loading && filtered.length === 0 && /* @__PURE__ */ jsxRuntimeExports.jsx(
            EmptyState,
            {
              title: catalog.remote.length === 0 ? "版本清单为空" : "没有匹配的版本",
              hint: catalog.remote.length === 0 ? "点「缓存」重新拉取官方清单（需要网络）。" : "换个关键词或切换上面的版本类型。",
              action: /* @__PURE__ */ jsxRuntimeExports.jsxs("button", { className: "btn", onClick: () => void load(), children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(IconRefresh, { size: 14 }),
                " 重新拉取"
              ] })
            }
          ),
          !loading && filtered.map((v2) => {
            const installed = installedIds.has(v2.id);
            const installing = installingId === v2.id;
            const dimmed = busy && !installing;
            return /* @__PURE__ */ jsxRuntimeExports.jsxs(
              "div",
              {
                className: `row row-static${installing ? " row-active" : ""}`,
                style: { opacity: dimmed ? 0.5 : 1 },
                children: [
                  /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "icon32", children: /* @__PURE__ */ jsxRuntimeExports.jsx("img", { src: iconFor(v2), alt: "", width: 32, height: 32 }) }),
                  /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minWidth: 0 }, children: [
                    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-strong truncate", children: v2.id }),
                    /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-meta", children: [
                      releaseDate(v2.releaseTime),
                      " · ",
                      typeLabel(v2)
                    ] })
                  ] }),
                  installing ? /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { width: 150, flexShrink: 0, display: "flex", alignItems: "center" }, children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                    ProgressBar,
                    {
                      value: progress !== null && progress.total > 0 ? Math.min(100, progress.current / progress.total * 100) : void 0,
                      indeterminate: progress !== null && progress.total === 0
                    }
                  ) }) : installed ? /* @__PURE__ */ jsxRuntimeExports.jsx(
                    "span",
                    {
                      className: "chip",
                      style: { color: "var(--success)", background: "transparent", border: "none" },
                      children: "已安装"
                    }
                  ) : /* @__PURE__ */ jsxRuntimeExports.jsx(
                    "button",
                    {
                      className: "btn btn-sm",
                      disabled: dimmed,
                      onClick: () => void install(v2.id),
                      children: "↓ 安装"
                    }
                  )
                ]
              },
              v2.id
            );
          })
        ] }) })
      ] })
    ] }),
    host
  ] });
}
function formatBytes(bytes) {
  if (!bytes || bytes <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
}
function lastPlayedText(iso) {
  if (iso === void 0 || iso === null || iso === "") return "无游玩记录";
  const d = new Date(iso);
  if (isNaN(d.getTime())) return "无游玩记录";
  const pad = (n2) => n2.toString().padStart(2, "0");
  const hm = `${pad(d.getHours())}:${pad(d.getMinutes())}`;
  const today = /* @__PURE__ */ new Date();
  const dayDiff = Math.floor(
    (new Date(today.getFullYear(), today.getMonth(), today.getDate()).getTime() - new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime()) / 864e5
  );
  if (dayDiff <= 0) return `今天 ${hm}`;
  if (dayDiff === 1) return `昨天 ${hm}`;
  if (dayDiff < 30) return `${dayDiff} 天前`;
  if (dayDiff < 365) return `${Math.floor(dayDiff / 30)} 个月前`;
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}
function SavesPage() {
  const { push, host } = useToasts();
  const [saves, setSaves] = reactExports.useState([]);
  const [loading, setLoading] = reactExports.useState(true);
  const [icons, setIcons] = reactExports.useState({});
  const [selected, setSelected] = reactExports.useState(/* @__PURE__ */ new Set());
  const [query, setQuery] = reactExports.useState("");
  const [versionFilter, setVersionFilter] = reactExports.useState("全部");
  const [busy, setBusy] = reactExports.useState(false);
  const load = reactExports.useCallback(async () => {
    setLoading(true);
    try {
      const data = await request("saves.list");
      setSaves(data.saves);
      for (const s of data.saves) {
        if (s.iconPath === void 0 || s.iconPath === "") continue;
        void request("saves.icon", { path: s.iconPath }).then((r2) => setIcons((prev) => ({ ...prev, [s.folderName]: r2.dataUrl }))).catch(() => void 0);
      }
    } catch (e) {
      push("error", `存档列表读取失败：${String(e)}`);
    }
    setLoading(false);
  }, [push]);
  reactExports.useEffect(() => {
    void load();
  }, [load]);
  const versions = reactExports.useMemo(() => {
    const set = /* @__PURE__ */ new Set();
    for (const s of saves) set.add(s.versionId === "" ? "全局" : s.versionId);
    return ["全部", ...set];
  }, [saves]);
  const filtered = reactExports.useMemo(() => {
    const q2 = query.trim().toLowerCase();
    return saves.filter((s) => {
      if (q2 !== "" && !`${s.levelName} ${s.folderName} ${s.versionId}`.toLowerCase().includes(q2)) {
        return false;
      }
      if (versionFilter !== "全部") {
        const v2 = s.versionId === "" ? "全局" : s.versionId;
        if (v2 !== versionFilter) return false;
      }
      return true;
    });
  }, [saves, query, versionFilter]);
  const selectedSaves = filtered.filter((s) => selected.has(s.folderName));
  async function openFolder(save) {
    setBusy(true);
    try {
      await request("saves.openFolder", { gameRoot: save?.gameRoot, folderName: save?.folderName });
      push("success", save === void 0 ? "已打开存档目录" : `已打开 ${save.levelName} 的目录`);
    } catch (e) {
      push("error", `打开目录失败：${String(e)}`);
    } finally {
      setBusy(false);
    }
  }
  async function backup(targets) {
    if (targets.length === 0) {
      push("info", "先勾选要备份的世界");
      return;
    }
    setBusy(true);
    try {
      let total = 0;
      for (const t2 of targets) {
        const r2 = await request("saves.backup", {
          gameRoot: t2.gameRoot,
          folderName: t2.folderName
        });
        total += r2.sizeBytes;
      }
      push("success", `已备份 ${targets.length} 个世界（${formatBytes(total)}）`);
    } catch (e) {
      push("error", `备份失败：${String(e)}`);
    } finally {
      setBusy(false);
    }
  }
  function toggle(folder) {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(folder)) next.delete(folder);
      else next.add(folder);
      return next;
    });
  }
  const tplBtn = (label, onClick, opts) => /* @__PURE__ */ jsxRuntimeExports.jsx(
    "button",
    {
      className: "btn",
      disabled: opts?.disabled === true || busy,
      title: opts?.title ?? label,
      onClick,
      children: label
    }
  );
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page", children: [
    /* @__PURE__ */ jsxRuntimeExports.jsxs(
      "div",
      {
        style: {
          display: "flex",
          gap: 8,
          alignItems: "center",
          marginBottom: 14,
          flexWrap: "wrap",
          flexShrink: 0
        },
        children: [
          tplBtn("用其他版本启动", () => push("info", "跨版本启动：需选择版本后可用"), {
            disabled: true
          }),
          tplBtn("复制到其他版本", () => push("info", "复制存档：内核能力接入后可用"), {
            disabled: true
          }),
          tplBtn(
            "备份所选世界",
            () => void backup(selectedSaves),
            { disabled: selectedSaves.length === 0, title: "备份勾选的世界" }
          ),
          tplBtn("同步到设备…", () => push("info", "设备同步：需登录 Starcloud ID"), {
            disabled: true
          }),
          tplBtn("分享", () => push("info", "分享：内核能力接入后可用"), { disabled: true }),
          /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { width: 1, height: 24, background: "var(--stroke-divider)" } }),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "input",
            {
              className: "input",
              style: { width: 220 },
              placeholder: "搜索世界 / 版本",
              value: query,
              onChange: (e) => setQuery(e.target.value)
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            Select,
            {
              width: 150,
              value: versionFilter,
              options: versions.map((v2) => ({ value: v2, label: v2 })),
              onChange: setVersionFilter
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { flex: 1 } }),
          tplBtn("导入分享包", () => push("info", "导入分享包：内核能力接入后可用"), {
            disabled: true
          }),
          tplBtn("打开存档目录", () => void openFolder()),
          /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn btn-subtle", onClick: () => void load(), disabled: loading, children: /* @__PURE__ */ jsxRuntimeExports.jsx(IconRefresh, { size: 15 }) })
        ]
      }
    ),
    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "card card-flush", style: { flex: 1, minHeight: 0 }, children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minHeight: 0, overflowY: "auto", overflowX: "hidden", padding: "6px 12px 12px" }, children: [
      loading && /* @__PURE__ */ jsxRuntimeExports.jsx(SkeletonList, { rows: 5, avatar: true, tall: true }),
      !loading && filtered.length === 0 && /* @__PURE__ */ jsxRuntimeExports.jsx(
        EmptyState,
        {
          title: saves.length === 0 ? "还没有发现存档" : "没有匹配的世界",
          hint: saves.length === 0 ? "启动一次游戏后回到这里，或确认设置里的 .minecraft 目录是否正确。" : "换个关键词或版本筛选试试。",
          action: /* @__PURE__ */ jsxRuntimeExports.jsxs("button", { className: "btn", onClick: () => void load(), children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx(IconRefresh, { size: 14 }),
            " 重新扫描"
          ] })
        }
      ),
      filtered.map((s) => {
        const icon = icons[s.folderName];
        const checked = selected.has(s.folderName);
        return /* @__PURE__ */ jsxRuntimeExports.jsxs(
          "div",
          {
            className: `row${checked ? " row-active" : ""}`,
            onClick: () => toggle(s.folderName),
            children: [
              /* @__PURE__ */ jsxRuntimeExports.jsx(
                "div",
                {
                  className: "icon32",
                  style: { width: 64, height: 64, borderRadius: 8, overflow: "hidden" },
                  children: icon !== void 0 ? /* @__PURE__ */ jsxRuntimeExports.jsx("img", { src: icon, alt: "", width: 64, height: 64, style: { objectFit: "cover" } }) : /* @__PURE__ */ jsxRuntimeExports.jsx(IconWorld, { size: 22, style: { opacity: 0.45 } })
                }
              ),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minWidth: 0 }, children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-strong truncate", children: s.levelName }),
                /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-meta truncate", children: s.folderName })
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 6, alignItems: "center" }, children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "chip", children: s.versionId === "" ? "全局" : s.versionId }),
                s.isolated && /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "chip", children: "隔离" })
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "meta", children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "meta-k", children: "上次游玩" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "meta-v", children: lastPlayedText(s.lastPlayed) }),
                /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "meta-k", children: "大小" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "meta-v", children: formatBytes(s.sizeBytes) })
              ] })
            ]
          },
          s.gameRoot + "/" + s.folderName
        );
      })
    ] }) }),
    host
  ] });
}
function Expander(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", style: { padding: 0, marginBottom: 10, overflow: "hidden" }, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsxs(
      "button",
      {
        className: "nav-item",
        style: { height: 52, width: "100%", borderRadius: 0, justifyContent: "space-between" },
        onClick: props.onToggle,
        children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-body", style: { color: "var(--text-primary)", fontSize: 14 }, children: props.title }),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            IconChevronRight,
            {
              size: 15,
              style: { opacity: 0.55, transform: props.open ? "rotate(90deg)" : "none" }
            }
          )
        ]
      }
    ),
    props.open && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { padding: "4px 16px 14px" }, children: props.children })
  ] });
}
const PHASE_LABEL = {
  idle: "空闲",
  hosting: "房间已创建",
  joining: "正在加入",
  connected: "已连接",
  failed: "出错",
  stopping: "正在停止"
};
function MultiplayerPage() {
  const { push, host } = useToasts();
  const [snap, setSnap] = reactExports.useState(null);
  const [busy, setBusy] = reactExports.useState(false);
  const [logs, setLogs] = reactExports.useState([]);
  const [logsOpen, setLogsOpen] = reactExports.useState(false);
  const [stepsOpen, setStepsOpen] = reactExports.useState(false);
  const [sdkOpen, setSdkOpen] = reactExports.useState(false);
  const [advancedOpen, setAdvancedOpen] = reactExports.useState(false);
  const [joinOpen, setJoinOpen] = reactExports.useState(false);
  const [joinCode, setJoinCode] = reactExports.useState("");
  const [nickname, setNickname] = reactExports.useState("");
  const [installProgress, setInstallProgress] = reactExports.useState(null);
  const pollRef = reactExports.useRef(null);
  const load = reactExports.useCallback(async () => {
    try {
      const s = await request("multiplayer.state");
      setSnap(s);
      setNickname((prev) => prev === "" ? s.nickname : prev);
    } catch (e) {
      push("error", `读取联机状态失败：${String(e)}`);
    }
  }, [push]);
  reactExports.useEffect(() => {
    void load();
    pollRef.current = window.setInterval(() => void load(), 2500);
    return () => {
      if (pollRef.current !== null) window.clearInterval(pollRef.current);
    };
  }, [load]);
  reactExports.useEffect(() => {
    onBridgeEvent((event, params) => {
      if (event === "multiplayer.changed") {
        void load();
      } else if (event === "multiplayer.install") {
        const pct = Number(params.percent ?? 0);
        setInstallProgress(pct > 0 ? pct : null);
      } else if (event === "multiplayer.installed") {
        setInstallProgress(null);
        push("success", "联机核心已安装");
        void load();
      }
    });
  }, []);
  async function run(method, params, hint) {
    if (busy) return;
    setBusy(true);
    try {
      await request(method, params);
      push("info", hint);
      await load();
    } catch (e) {
      push("error", String(e));
    } finally {
      setBusy(false);
    }
  }
  async function openLogs() {
    try {
      const r2 = await request("multiplayer.logs");
      setLogs(r2.logs);
      setLogsOpen(true);
    } catch (e) {
      push("error", `日志读取失败：${String(e)}`);
    }
  }
  const providers = snap?.providers ?? [];
  const activeId = snap?.activeId ?? "";
  const active = providers.find((p2) => p2.id === activeId);
  const coreVersion = snap?.core.installed === true && snap.core.version !== void 0 ? snap.core.version : "未安装";
  const phase = snap?.state.phase ?? "idle";
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page", children: [
    /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page-head", children: [
      /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { flex: 1 } }),
      /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-cap", children: "联机方案" }),
      /* @__PURE__ */ jsxRuntimeExports.jsx(
        Select,
        {
          width: 220,
          value: activeId,
          disabled: busy || snap?.state.isBusy === true,
          placeholder: "（无可用方案）",
          options: providers.length === 0 ? [{ value: "", label: "（无可用方案）" }] : providers.map((p2) => ({ value: p2.id, label: p2.displayName })),
          onChange: (id2) => void run("multiplayer.selectProvider", { id: id2 }, "已切换联机方案")
        }
      )
    ] }),
    /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page-body", children: [
      /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", style: { marginBottom: 12 }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 40, flexWrap: "wrap" }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: "联机核心" }),
            /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-body", style: { color: "var(--text-primary)" }, children: [
              "v",
              coreVersion
            ] })
          ] }),
          /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: "核心会话" }),
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-body", style: { color: "var(--text-primary)" }, children: snap?.core.sessionRunning === true ? "运行中" : "已停止" })
          ] }),
          /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: "当前状态" }),
            /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-body", style: { color: "var(--text-primary)" }, children: [
              PHASE_LABEL[phase] ?? phase,
              snap?.state.isBusy === true ? "（处理中）" : ""
            ] })
          ] })
        ] }),
        snap?.core.installed === false && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "info info-warning", style: { marginTop: 12 }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx(IconWarning, { size: 14 }),
          /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "info-body", style: { flex: 1 }, children: [
            "联机核心未安装。",
            installProgress !== null && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { marginTop: 6 }, children: /* @__PURE__ */ jsxRuntimeExports.jsx(ProgressBar, { value: installProgress }) })
          ] }),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn btn-sm",
              disabled: installProgress !== null,
              onClick: () => void request("multiplayer.install").then(() => push("info", "已开始下载联机核心")).catch((e) => push("error", String(e))),
              children: "安装"
            }
          )
        ] }),
        /* @__PURE__ */ jsxRuntimeExports.jsxs(
          "button",
          {
            className: "nav-item",
            style: {
              width: "100%",
              marginTop: 12,
              background: "var(--surface-subtle)",
              justifyContent: "space-between",
              paddingLeft: 12
            },
            onClick: () => setAdvancedOpen((v2) => !v2),
            children: [
              /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-body", style: { color: "var(--text-primary)" }, children: "高级选项" }),
              /* @__PURE__ */ jsxRuntimeExports.jsx("span", { style: { opacity: 0.55, transform: advancedOpen ? "rotate(90deg)" : "none" }, children: /* @__PURE__ */ jsxRuntimeExports.jsx(IconChevronRight, { size: 15 }) })
            ]
          }
        ),
        advancedOpen && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { marginTop: 12, display: "flex", gap: 12, flexWrap: "wrap" }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsxs("label", { style: { flex: 1, minWidth: 220 }, children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", style: { marginBottom: 4 }, children: "昵称" }),
            /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 8 }, children: [
              /* @__PURE__ */ jsxRuntimeExports.jsx(
                "input",
                {
                  className: "input",
                  style: { flex: 1 },
                  value: nickname,
                  onChange: (e) => setNickname(e.target.value)
                }
              ),
              /* @__PURE__ */ jsxRuntimeExports.jsx(
                "button",
                {
                  className: "btn",
                  disabled: busy || nickname.trim() === "",
                  onClick: () => void run("multiplayer.setNickname", { name: nickname.trim() }, "昵称已更新"),
                  children: "保存"
                }
              )
            ] })
          ] }),
          /* @__PURE__ */ jsxRuntimeExports.jsxs("label", { style: { minWidth: 180 }, children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", style: { marginBottom: 4 }, children: "节点地区" }),
            /* @__PURE__ */ jsxRuntimeExports.jsx(
              Select,
              {
                width: 160,
                value: snap?.nodeRegion ?? "auto",
                options: [
                  { value: "auto", label: "自动" },
                  { value: "cn", label: "中国" },
                  { value: "overseas", label: "海外" }
                ],
                onChange: (region) => void run("multiplayer.setNodeRegion", { region }, "节点已切换")
              }
            )
          ] })
        ] })
      ] }),
      /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", style: { display: "flex", gap: 10, marginBottom: 12, flexWrap: "wrap" }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx(
          "button",
          {
            className: "btn btn-primary",
            disabled: busy || snap?.core.installed === false,
            onClick: () => void run("multiplayer.host", {}, "已开始创建房间，等状态变成「房间已创建」"),
            children: "创建房间"
          }
        ),
        /* @__PURE__ */ jsxRuntimeExports.jsx(
          "button",
          {
            className: "btn",
            disabled: busy || snap?.core.installed === false,
            onClick: () => setJoinOpen(true),
            children: "加入好友的房间"
          }
        ),
        /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: () => void openLogs(), children: "查看日志" }),
        (phase === "hosting" || phase === "joining" || phase === "connected") && /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn",
              disabled: busy,
              onClick: () => void run("multiplayer.stop", {}, "已停止联机会话"),
              children: "停止会话"
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsxs(
            "button",
            {
              className: "btn btn-subtle",
              disabled: busy,
              onClick: () => void run("multiplayer.idle", {}, "已回到空闲"),
              children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(IconRefresh, { size: 14 }),
                " 回到空闲"
              ]
            }
          )
        ] }),
        /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { flex: 1 } }),
        snap?.state.room !== void 0 && snap.state.room !== "" && /* @__PURE__ */ jsxRuntimeExports.jsxs("span", { className: "chip chip-accent", children: [
          "邀请码 ",
          snap.state.room
        ] })
      ] }),
      /* @__PURE__ */ jsxRuntimeExports.jsx(Expander, { title: "联机核心日志", open: logsOpen, onToggle: () => setLogsOpen((v2) => !v2), children: /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "log", style: { maxHeight: 240 }, children: logs.length === 0 ? "暂无日志" : logs.join("\n") }) }),
      /* @__PURE__ */ jsxRuntimeExports.jsx(Expander, { title: "使用步骤", open: stepsOpen, onToggle: () => setStepsOpen((v2) => !v2), children: /* @__PURE__ */ jsxRuntimeExports.jsxs("ol", { className: "t-body", style: { margin: 0, paddingLeft: 18, lineHeight: 1.9 }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx("li", { children: "点「创建房间」，等状态变成「房间已创建」" }),
        /* @__PURE__ */ jsxRuntimeExports.jsx("li", { children: "把邀请码发给好友" }),
        /* @__PURE__ */ jsxRuntimeExports.jsx("li", { children: "好友点「加入好友的房间」填邀请码" }),
        /* @__PURE__ */ jsxRuntimeExports.jsx("li", { children: "双方启动游戏，在多人游戏里连接本地房间" })
      ] }) }),
      /* @__PURE__ */ jsxRuntimeExports.jsx(Expander, { title: "第三方联机 SDK", open: sdkOpen, onToggle: () => setSdkOpen((v2) => !v2), children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-body", children: [
        active === void 0 ? "当前没有可用的联机方案。" : `${active.displayName}（${active.vendor}）—— ${active.description}`,
        active?.vendorUrl !== void 0 && active.vendorUrl !== "" && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-meta", style: { marginTop: 6 }, children: active.vendorUrl })
      ] }) }),
      /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", style: { textAlign: "center", marginTop: 16 }, children: "联机由 Terracotta | 陶瓦联机 提供技术支持" })
    ] }),
    /* @__PURE__ */ jsxRuntimeExports.jsx(
      Modal,
      {
        open: joinOpen,
        title: "加入好友的房间",
        onClose: () => setJoinOpen(false),
        footer: /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: () => setJoinOpen(false), children: "取消" }),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn btn-primary",
              disabled: joinCode.trim() === "" || busy,
              onClick: () => {
                setJoinOpen(false);
                void run("multiplayer.join", { room: joinCode.trim() }, "已提交进房请求");
                setJoinCode("");
              },
              children: "加入"
            }
          )
        ] }),
        children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", flexDirection: "column", gap: 8 }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-cap", children: "邀请码" }),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "input",
            {
              className: "input",
              autoFocus: true,
              value: joinCode,
              placeholder: "粘贴好友发来的邀请码",
              onChange: (e) => setJoinCode(e.target.value)
            }
          )
        ] })
      }
    ),
    /* @__PURE__ */ jsxRuntimeExports.jsx(Modal, { open: logsOpen && false, title: "联机核心日志", onClose: () => setLogsOpen(false), children: /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "log", style: { maxHeight: "50vh" }, children: logs.length === 0 ? "暂无日志" : logs.join("\n") }) }),
    host
  ] });
}
function chunks(items, size) {
  const out = [];
  for (let i = 0; i < items.length; i += size) out.push(items.slice(i, i + size));
  return out;
}
function ProjectionViewer(props) {
  const frameRef = reactExports.useRef(null);
  const [ready, setReady] = reactExports.useState(false);
  const [loading, setLoading] = reactExports.useState(false);
  const [status, setStatus] = reactExports.useState("等待投影数据…");
  const pathRef = reactExports.useRef(props.path);
  reactExports.useEffect(() => {
    const onMessage = (e) => {
      const data = e.data;
      if (data !== null && typeof data === "object" && data.type === "ready") setReady(true);
    };
    window.addEventListener("message", onMessage);
    return () => window.removeEventListener("message", onMessage);
  }, []);
  reactExports.useEffect(() => {
    pathRef.current = props.path;
    if (!ready || props.path === void 0 || props.path === "") return;
    const target = props.path;
    let cancelled = false;
    async function push() {
      setLoading(true);
      setStatus("正在解析投影与资源…");
      try {
        const payload = await request("lab.assets", { path: target });
        if (cancelled || pathRef.current !== target) return;
        const win = frameRef.current?.contentWindow;
        if (win === void 0 || win === null) return;
        win.postMessage(
          {
            type: "voxelBegin",
            sizeX: payload.sizeX,
            sizeY: payload.sizeY,
            sizeZ: payload.sizeZ,
            truncated: payload.truncated,
            assetSource: payload.assetSource ?? ""
          },
          "*"
        );
        for (const part of chunks(Object.entries(payload.states), 200)) {
          win.postMessage({ type: "voxelStates", items: Object.fromEntries(part) }, "*");
        }
        for (const part of chunks(Object.entries(payload.models), 200)) {
          win.postMessage({ type: "voxelModels", items: Object.fromEntries(part) }, "*");
        }
        for (const part of chunks(Object.entries(payload.textures), 60)) {
          win.postMessage({ type: "voxelTextures", items: Object.fromEntries(part) }, "*");
        }
        for (const part of chunks(payload.blocks, 2e4)) {
          win.postMessage({ type: "voxelBlocks", items: part }, "*");
        }
        win.postMessage({ type: "voxelEnd" }, "*");
        setStatus("");
      } catch (e) {
        if (cancelled) return;
        setStatus("");
        props.onError?.(String(e));
      } finally {
        if (!cancelled) setLoading(false);
      }
    }
    void push();
    return () => {
      cancelled = true;
    };
  }, [ready, props.path]);
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { position: "relative", width: "100%", height: "100%" }, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx(
      "iframe",
      {
        ref: frameRef,
        title: "投影预览",
        src: "./lab/projection-viewer.html",
        onLoad: () => setReady(true),
        style: { width: "100%", height: "100%", border: "none", display: "block" }
      }
    ),
    (loading || status !== "") && /* @__PURE__ */ jsxRuntimeExports.jsx(
      "div",
      {
        style: {
          position: "absolute",
          left: 12,
          top: 12,
          padding: "6px 10px",
          borderRadius: "var(--r-sm)",
          background: "var(--surface-acrylic)",
          backdropFilter: "blur(12px)",
          fontSize: 12,
          color: "var(--text-secondary)",
          pointerEvents: "none"
        },
        children: loading ? "正在解析投影与资源…" : status
      }
    )
  ] });
}
const MAX_STATS_ROWS = 30;
function blockColor(id2) {
  let hash = 0;
  for (let i = 0; i < id2.length; i += 1) {
    hash = hash * 31 + id2.charCodeAt(i) >>> 0;
  }
  const hue = hash % 360;
  const sat = 45 + (hash >> 9) % 25;
  const light = 46 + (hash >> 17) % 16;
  return `hsl(${hue} ${sat}% ${light}%)`;
}
function shade(color, delta) {
  return color.replace(/(\d+(?:\.\d+)?)%\)$/, (_m, raw) => {
    const next = Math.max(0, Math.min(100, Number.parseFloat(raw) + delta));
    return `${next}%)`;
  });
}
function LabPage() {
  const { push, host } = useToasts();
  const [path, setPath] = reactExports.useState("");
  const [preview, setPreview] = reactExports.useState();
  const [loading, setLoading] = reactExports.useState(false);
  const [generating, setGenerating] = reactExports.useState(false);
  const [error, setError] = reactExports.useState("");
  const [section, setSection] = reactExports.useState("workshop");
  const [projFiles, setProjFiles] = reactExports.useState([]);
  const [schematicsDir, setSchematicsDir] = reactExports.useState("");
  const canvasRef = reactExports.useRef(null);
  const viewRef = reactExports.useRef({ zoom: 1, offsetX: 0, offsetY: 0 });
  reactExports.useRef(null);
  const [dragging, setDragging] = reactExports.useState(false);
  const [drawToken, setDrawToken] = reactExports.useState(0);
  const redraw = reactExports.useCallback(() => setDrawToken((n2) => n2 + 1), []);
  const scanProjections = reactExports.useCallback(async () => {
    try {
      const r2 = await request("projections.scan", {});
      setProjFiles(r2.files);
      const first = r2.files[0];
      if (first !== void 0) {
        setSchematicsDir(first.filePath.replace(/[\/][^\/]+$/, ""));
      }
    } catch {
    }
  }, []);
  reactExports.useEffect(() => {
    void scanProjections();
  }, [scanProjections]);
  const generateWorldEater = async () => {
    setGenerating(true);
    try {
      const result = await request("lab.worldEater", {
        length: 64,
        width: 16,
        layers: 3,
        spacing: 2,
        layerGap: 1,
        offset: 2
      });
      push("success", `世吞模板已生成：${result.path}`);
      setPath(result.path);
    } catch (e) {
      push("error", `生成失败：${String(e)}`);
    } finally {
      setGenerating(false);
    }
  };
  reactExports.useEffect(() => {
    const canvas = canvasRef.current;
    if (canvas == null || preview == null) return;
    const blocks = preview.preview;
    if (blocks.length === 0) return;
    const ctx = canvas.getContext("2d");
    if (ctx == null) return;
    const dpr = window.devicePixelRatio || 1;
    const rect = canvas.getBoundingClientRect();
    const width = Math.max(1, Math.floor(rect.width));
    const height = Math.max(1, Math.floor(rect.height));
    if (canvas.width !== width * dpr || canvas.height !== height * dpr) {
      canvas.width = width * dpr;
      canvas.height = height * dpr;
    }
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, width, height);
    const unit = 7;
    const cos30 = Math.cos(Math.PI / 6);
    const sin30 = Math.sin(Math.PI / 6);
    const view = viewRef.current;
    const sorted = [...blocks].sort(
      (a, b) => a.x + a.y + a.z - (b.x + b.y + b.z)
    );
    const toScreen = (vx, vy, vz) => ({
      sx: width / 2 + (vx - vz) * cos30 * unit * view.zoom + view.offsetX,
      sy: height / 2 + (vx + vz) * sin30 * unit * view.zoom - vy * unit * view.zoom + view.offsetY
    });
    for (const b of sorted) {
      const color = blockColor(b.id);
      const step = unit * view.zoom;
      const hw = cos30 * step;
      const top = toScreen(b.x, b.y + 1, b.z);
      ctx.beginPath();
      ctx.moveTo(top.sx, top.sy - sin30 * step);
      ctx.lineTo(top.sx + hw, top.sy);
      ctx.lineTo(top.sx, top.sy + sin30 * step);
      ctx.lineTo(top.sx - hw, top.sy);
      ctx.closePath();
      ctx.fillStyle = shade(color, 14);
      ctx.fill();
      ctx.beginPath();
      ctx.moveTo(top.sx + hw, top.sy);
      ctx.lineTo(top.sx, top.sy + sin30 * step);
      ctx.lineTo(top.sx, top.sy + sin30 * step + step);
      ctx.lineTo(top.sx + hw, top.sy + step);
      ctx.closePath();
      ctx.fillStyle = shade(color, -18);
      ctx.fill();
    }
  }, [preview, drawToken]);
  reactExports.useEffect(() => {
    const canvas = canvasRef.current;
    if (canvas == null) return;
    const observer = new ResizeObserver(() => redraw());
    observer.observe(canvas);
    return () => observer.disconnect();
  }, [redraw]);
  const resetView = () => {
    viewRef.current = { zoom: 1, offsetX: 0, offsetY: 0 };
    redraw();
  };
  const voxels = preview?.preview ?? [];
  const stats = (preview?.stats ?? []).slice(0, MAX_STATS_ROWS);
  const maxCount = stats.length > 0 ? Math.max(...stats.map((s) => s.count)) : 1;
  return /* @__PURE__ */ jsxRuntimeExports.jsx(jsxRuntimeExports.Fragment, { children: /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "page", children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minHeight: 0, display: "flex", gap: 16 }, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "card", style: { width: 220, flexShrink: 0, padding: 8, alignSelf: "flex-start" }, children: [
      { id: "workshop", label: "投影工坊", icon: /* @__PURE__ */ jsxRuntimeExports.jsx(IconList, { size: 15 }) },
      { id: "color", label: "彩色文字", icon: /* @__PURE__ */ jsxRuntimeExports.jsx(IconSettings, { size: 15 }) },
      { id: "community", label: "投影社区", icon: /* @__PURE__ */ jsxRuntimeExports.jsx(IconWorld, { size: 15 }) },
      { id: "command", label: "指令助手", icon: /* @__PURE__ */ jsxRuntimeExports.jsx(IconImport, { size: 15 }) }
    ].map((t2) => /* @__PURE__ */ jsxRuntimeExports.jsxs(
      "button",
      {
        className: `nav-item${section === t2.id ? " nav-active" : ""}`,
        style: { height: 40, width: "100%" },
        onClick: () => {
          setSection(t2.id);
          if (t2.id !== "workshop") push("info", `${t2.label}：实验室功能开发中`);
        },
        children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "nav-icon", style: { fontFamily: "inherit", fontSize: 14 }, children: t2.icon }),
          /* @__PURE__ */ jsxRuntimeExports.jsx("span", { children: t2.label })
        ]
      },
      t2.id
    )) }),
    /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minWidth: 0, minHeight: 0, overflowY: "auto" }, children: [
      /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", style: { marginBottom: 14 }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", alignItems: "center", gap: 10, marginBottom: 10 }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "span",
            {
              style: { width: 4, height: 16, borderRadius: 2, background: "var(--accent)" }
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-strong", style: { fontSize: 15 }, children: "投影工坊" }),
          /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-cap truncate", children: schematicsDir })
        ] }),
        /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 8, marginBottom: 12, flexWrap: "wrap" }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: () => void scanProjections(), children: "刷新列表" }),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn",
              onClick: () => push("info", "打开文件：需要主进程文件对话框，接入后可用"),
              children: "打开文件…"
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn",
              onClick: () => push("info", "打开投影目录：需要主进程 openPath，接入后可用"),
              children: "打开投影目录"
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { flex: 1 } }),
          /* @__PURE__ */ jsxRuntimeExports.jsxs(
            "button",
            {
              className: "btn btn-subtle",
              disabled: generating,
              onClick: () => void generateWorldEater(),
              children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(IconList, { size: 14 }),
                " ",
                generating ? "生成中…" : "生成世吞模板"
              ]
            }
          )
        ] }),
        /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { maxHeight: 220, overflowY: "auto" }, children: [
          projFiles.length === 0 && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: "暂无投影文件（schematics 目录为空）。" }),
          projFiles.map((f2) => /* @__PURE__ */ jsxRuntimeExports.jsxs(
            "button",
            {
              className: `row${path === f2.filePath ? " row-active" : ""}`,
              style: { width: "100%", textAlign: "left", minHeight: 0, paddingTop: 8, paddingBottom: 8 },
              onClick: () => {
                setPath(f2.filePath);
                void (async () => {
                  setLoading(true);
                  try {
                    const result = await request("lab.load", {
                      path: f2.filePath
                    });
                    setPreview(result);
                    viewRef.current = { zoom: 1, offsetX: 0, offsetY: 0 };
                    redraw();
                  } catch (e) {
                    setError(String(e));
                  } finally {
                    setLoading(false);
                  }
                })();
              },
              children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-body truncate", style: { flex: 1, color: "var(--text-primary)" }, children: f2.fileName }),
                /* @__PURE__ */ jsxRuntimeExports.jsxs("span", { className: "t-meta", style: { flexShrink: 0 }, children: [
                  (f2.sizeBytes / 1024).toFixed(0),
                  " KB ·",
                  " ",
                  new Date(f2.modifiedAt).toLocaleDateString()
                ] })
              ]
            },
            f2.filePath
          ))
        ] })
      ] }),
      loading && /* @__PURE__ */ jsxRuntimeExports.jsx(SkeletonList, { rows: 3 }),
      error !== "" && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "info info-danger", style: { marginBottom: 12 }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx(IconClose, { size: 15 }),
        /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "info-body", style: { fontSize: 12 }, children: error })
      ] }),
      preview != null && preview.unsupported != null && preview.unsupported.length > 0 && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "info info-warning", style: { marginBottom: 14 }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx(IconWarning, { size: 15 }),
        /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "info-body t-body", children: [
          "不支持的投影格式：",
          preview.unsupported
        ] })
      ] }),
      preview != null && /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
        /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", style: { marginBottom: 14, padding: "14px 16px" }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", alignItems: "center", gap: 10 }, children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-strong", children: "投影统计" }),
            /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-cap truncate", children: preview.path })
          ] }),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "div",
            {
              style: {
                display: "grid",
                gridTemplateColumns: "repeat(auto-fit, minmax(130px, 1fr))",
                gap: 8,
                marginTop: 10
              },
              children: [
                { label: "尺寸 X", value: `${preview.sizeX}` },
                { label: "尺寸 Y", value: `${preview.sizeY}` },
                { label: "尺寸 Z", value: `${preview.sizeZ}` },
                { label: "方块总数", value: `${preview.totalBlocks}` },
                { label: "方块种类", value: `${preview.distinctBlocks}` },
                {
                  label: "预览采样",
                  value: `${voxels.length}${preview.previewTruncated ? " ↓" : ""}`
                }
              ].map((cell, index) => /* @__PURE__ */ jsxRuntimeExports.jsxs(
                "div",
                {
                  style: {
                    padding: "10px 12px",
                    borderRadius: "var(--r-card)",
                    background: "var(--surface-subtle)",
                    border: "1px solid var(--stroke-subtle)",
                    animation: `rowIn var(--t-enter) var(--e-enter) both`,
                    animationDelay: `${index * 39}ms`
                  },
                  children: [
                    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-meta", style: { display: "block" }, children: cell.label }),
                    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-strong", children: cell.value })
                  ]
                },
                cell.label
              ))
            }
          ),
          (preview.truncated || preview.previewTruncated) && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-meta", style: { display: "block", marginTop: 8 }, children: [
            preview.truncated ? "文件方块数超出解析上限，统计与预览均基于已读取的部分。" : "",
            preview.previewTruncated ? "预览已按步长降采样，仅绘制部分方块（不是完整模型）。" : ""
          ] })
        ] }),
        /* @__PURE__ */ jsxRuntimeExports.jsxs(
          "div",
          {
            className: "card",
            style: {
              display: "flex",
              flexDirection: "column",
              gap: 10,
              padding: "14px 16px",
              marginBottom: 14
            },
            children: [
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", alignItems: "center", gap: 8 }, children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx("span", { style: { fontSize: 16 }, children: "◫" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-strong", children: "等距预览" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { flex: 1 } }),
                /* @__PURE__ */ jsxRuntimeExports.jsxs("button", { className: "btn btn-subtle", onClick: resetView, children: [
                  /* @__PURE__ */ jsxRuntimeExports.jsx(IconRefresh, { size: 14 }),
                  " 复位视图"
                ] })
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs(
                "div",
                {
                  style: {
                    position: "relative",
                    height: 420,
                    borderRadius: "var(--r-card)",
                    overflow: "hidden",
                    background: "radial-gradient(120% 100% at 50% 0%, var(--surface-mica-a) 0%, var(--surface-window) 100%)",
                    border: "1px solid var(--stroke-subtle)"
                  },
                  children: [
                    /* @__PURE__ */ jsxRuntimeExports.jsx(ProjectionViewer, { path, onError: (m2) => setError(m2) }),
                    "              "
                  ]
                }
              )
            ]
          }
        ),
        /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", style: { padding: "14px 16px" }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", alignItems: "center", gap: 10 }, children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-strong", children: "方块种类" }),
            /* @__PURE__ */ jsxRuntimeExports.jsxs("span", { className: "t-cap", children: [
              "(",
              stats.length,
              ")"
            ] }),
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { flex: 1 } }),
            preview.stats.length > MAX_STATS_ROWS && /* @__PURE__ */ jsxRuntimeExports.jsxs("span", { className: "t-meta", children: [
              "仅显示前 ",
              MAX_STATS_ROWS,
              " 条"
            ] })
          ] }),
          /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { marginTop: 8 }, children: stats.length === 0 ? /* @__PURE__ */ jsxRuntimeExports.jsx(EmptyState, { title: "没有方块数据", hint: "换一个投影文件再试试。" }) : stats.map((s, index) => /* @__PURE__ */ jsxRuntimeExports.jsxs(
            "div",
            {
              className: "row row-static",
              style: {
                padding: "7px 10px",
                margin: "0 4px",
                animationDelay: `${index * 39}ms`
              },
              children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  "div",
                  {
                    style: {
                      width: 18,
                      height: 18,
                      borderRadius: "var(--r-control)",
                      flexShrink: 0,
                      border: "1px solid var(--stroke-strong)",
                      background: blockColor(s.id)
                    }
                  }
                ),
                /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minWidth: 0 }, children: [
                  /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-body truncate", children: s.id }),
                  /* @__PURE__ */ jsxRuntimeExports.jsx(
                    "div",
                    {
                      style: {
                        height: 4,
                        borderRadius: 2,
                        background: "var(--surface-hover)",
                        overflow: "hidden",
                        marginTop: 4
                      },
                      children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                        "div",
                        {
                          style: {
                            width: `${s.count / maxCount * 100}%`,
                            height: "100%",
                            background: blockColor(s.id),
                            transition: "width 450ms cubic-bezier(0.1,0.9,0.2,1)"
                          }
                        }
                      )
                    }
                  )
                ] }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  "span",
                  {
                    className: "t-body",
                    style: { opacity: 0.75, minWidth: 72, textAlign: "right" },
                    children: s.count
                  }
                )
              ]
            },
            s.id
          )) })
        ] })
      ] }),
      !loading && preview == null && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "card", children: /* @__PURE__ */ jsxRuntimeExports.jsx(
        EmptyState,
        {
          title: "还没有载入投影",
          hint: "填一个 .litematic 的绝对路径点「加载预览」，或直接生成一份世吞模板试手气。",
          action: /* @__PURE__ */ jsxRuntimeExports.jsxs("button", { className: "btn", onClick: () => void generateWorldEater(), children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx(IconList, { size: 14 }),
            " 生成世吞模板"
          ] })
        }
      ) }),
      host
    ] })
  ] }) }) });
}
function fmtSize(bytes) {
  if (bytes >= 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  return `${Math.max(1, Math.round(bytes / 1024))} KB`;
}
function fmtTime(iso) {
  const d = new Date(iso);
  if (isNaN(d.getTime())) return iso;
  const p2 = (n2) => n2.toString().padStart(2, "0");
  return `${p2(d.getMonth() + 1)}-${p2(d.getDate())} ${p2(d.getHours())}:${p2(d.getMinutes())}`;
}
function ProjectionsPage() {
  const { push, host } = useToasts();
  const [files, setFiles] = reactExports.useState([]);
  const [versions, setVersions] = reactExports.useState([]);
  const [loading, setLoading] = reactExports.useState(true);
  const [busy, setBusy] = reactExports.useState(false);
  const [selected, setSelected] = reactExports.useState(null);
  const [dest, setDest] = reactExports.useState("");
  const [confirmDelete, setConfirmDelete] = reactExports.useState(null);
  const load = reactExports.useCallback(async () => {
    setLoading(true);
    try {
      const r2 = await request("projections.scan", {});
      setFiles(r2.files);
      setVersions(r2.versions);
      if (selected !== null && !r2.files.some((f2) => f2.filePath === selected)) {
        setSelected(null);
      }
    } catch (e) {
      push("error", `扫描投影失败：${String(e)}`);
    }
    setLoading(false);
  }, [push]);
  reactExports.useEffect(() => {
    void load();
  }, [load]);
  const current = reactExports.useMemo(
    () => files.find((f2) => f2.filePath === selected) ?? null,
    [files, selected]
  );
  async function transfer(mode) {
    if (current === null) return;
    setBusy(true);
    try {
      const r2 = await request(
        mode === "copy" ? "projections.copy" : "projections.move",
        { filePath: current.filePath, versionId: dest === "" ? null : dest }
      );
      push("success", `${mode === "copy" ? "已复制" : "已移动"}到 ${r2.path}`);
      await load();
    } catch (e) {
      push("error", String(e));
    } finally {
      setBusy(false);
    }
  }
  async function remove(file) {
    setBusy(true);
    try {
      await request("projections.delete", { filePath: file.filePath });
      push("success", `已删除 ${file.fileName}`);
      setSelected(null);
      await load();
    } catch (e) {
      push("error", String(e));
    } finally {
      setBusy(false);
    }
  }
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page", children: [
    /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page-head", children: [
      /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minWidth: 0 }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx("h1", { className: "page-title", children: "投影管理" }),
        /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "page-sub", children: "扫描全局与各版本实例的投影，可直接复制到需要使用的版本。" })
      ] }),
      /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn btn-primary", onClick: () => void load(), disabled: loading, children: "刷新" })
    ] }),
    /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minHeight: 0, display: "flex", gap: 16 }, children: [
      /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "card card-flush", style: { flex: 1, minWidth: 0 }, children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minHeight: 0, overflowY: "auto", padding: "8px 12px" }, children: [
        loading && /* @__PURE__ */ jsxRuntimeExports.jsx(SkeletonList, { rows: 6 }),
        !loading && files.length === 0 && /* @__PURE__ */ jsxRuntimeExports.jsx(
          EmptyState,
          {
            title: "还没有投影文件",
            hint: "投影放在游戏目录的 schematics 下（.litematic / .nbt / .schematic / .schem）。"
          }
        ),
        files.map((f2) => {
          const active = f2.filePath === selected;
          return /* @__PURE__ */ jsxRuntimeExports.jsxs(
            "div",
            {
              className: `row${active ? " row-active" : ""}`,
              style: { alignItems: "flex-start", paddingTop: 12, paddingBottom: 12 },
              onClick: () => setSelected(active ? null : f2.filePath),
              children: [
                /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minWidth: 0 }, children: [
                  /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-strong truncate", children: f2.fileName }),
                  /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-meta", children: [
                    fmtSize(f2.sizeBytes),
                    " · ",
                    fmtTime(f2.modifiedAt),
                    " ·",
                    " ",
                    f2.versionId === void 0 ? "全局目录" : `版本 ${f2.versionId}`
                  ] }),
                  /* @__PURE__ */ jsxRuntimeExports.jsx(
                    "div",
                    {
                      className: "t-meta truncate",
                      style: { opacity: 0.65 },
                      title: f2.filePath,
                      children: f2.filePath
                    }
                  )
                ] }),
                /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-body", style: { flexShrink: 0 }, children: f2.versionId === void 0 ? "全局" : f2.versionId })
              ]
            },
            f2.filePath
          );
        })
      ] }) }),
      /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", style: { width: 370, flexShrink: 0, alignSelf: "flex-start" }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-strong", style: { marginBottom: 12 }, children: "目标版本" }),
        /* @__PURE__ */ jsxRuntimeExports.jsx(
          Select,
          {
            width: 340,
            value: dest,
            options: [
              { value: "", label: "全局（共享目录）" },
              ...versions.map((v2) => ({ value: v2, label: v2 }))
            ],
            onChange: setDest
          }
        ),
        /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", flexDirection: "column", gap: 8, marginTop: 14 }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn",
              disabled: current === null || busy,
              onClick: () => void transfer("copy"),
              children: "复制到该版本"
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn",
              disabled: current === null || busy,
              onClick: () => void transfer("move"),
              children: "移动到该版本"
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn",
              disabled: current === null || busy,
              onClick: () => {
                if (current === null) return;
                void request("shell.openPath", { path: current.filePath }).catch(
                  (e) => push("error", String(e))
                );
              },
              children: "在文件夹中显示"
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn btn-danger",
              disabled: current === null || busy,
              onClick: () => {
                if (current !== null) setConfirmDelete(current);
              },
              children: "删除"
            }
          )
        ] }),
        current !== null && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-cap", style: { marginTop: 12 }, children: [
          "已选：",
          current.fileName,
          /* @__PURE__ */ jsxRuntimeExports.jsx("br", {}),
          "当前位于：",
          current.versionId === void 0 ? "全局目录" : current.versionId
        ] })
      ] })
    ] }),
    /* @__PURE__ */ jsxRuntimeExports.jsx(
      Modal,
      {
        open: confirmDelete !== null,
        title: "删除投影？",
        onClose: () => setConfirmDelete(null),
        footer: /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: () => setConfirmDelete(null), children: "取消" }),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn btn-primary",
              style: { background: "var(--danger)", color: "#fff" },
              onClick: () => {
                const f2 = confirmDelete;
                setConfirmDelete(null);
                if (f2 !== null) void remove(f2);
              },
              children: "删除"
            }
          )
        ] }),
        children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "dialog-body", children: [
          "投影文件会从磁盘永久删除：",
          confirmDelete?.fileName
        ] })
      }
    ),
    host
  ] });
}
function RunningPage(props) {
  const { push, host } = useToasts();
  const session = props.session;
  async function killGame() {
    if (session.sessionId === void 0) return;
    try {
      await request("game.kill", { sessionId: session.sessionId });
      push("info", "已结束游戏进程");
    } catch (e) {
      push("error", `结束失败：${String(e)}`);
    }
  }
  const running = session.pid !== void 0 && session.pid > 0;
  const hasLogs = session.logs.length > 0;
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page", children: [
    /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page-head", children: [
      /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { flex: 1 } }),
      /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn btn-subtle", title: "刷新", onClick: () => window.location.reload(), children: /* @__PURE__ */ jsxRuntimeExports.jsx(IconRefresh, { size: 15 }) }),
      /* @__PURE__ */ jsxRuntimeExports.jsxs("button", { className: "btn", title: "采样设置", children: [
        "采样设置 ",
        /* @__PURE__ */ jsxRuntimeExports.jsx(IconChevronDown, { size: 14 })
      ] })
    ] }),
    !running && session.status === "" && !hasLogs ? (
      // 空态（Win 端同款居中卡）
      /* @__PURE__ */ jsxRuntimeExports.jsx(
        "div",
        {
          className: "card",
          style: {
            flex: 1,
            minHeight: 0,
            display: "flex",
            alignItems: "center",
            justifyContent: "center"
          },
          children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "empty", style: { minHeight: 0 }, children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { fontSize: 26, opacity: 0.5, lineHeight: 1 }, children: "☰" }),
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "empty-title", style: { fontSize: 16 }, children: "现在没有游戏在运行" }),
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "empty-hint", children: "去首页启动一个版本" }),
            /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn btn-primary", onClick: props.onGoHome, children: "去首页" })
          ] })
        }
      )
    ) : /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
      /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "card", style: { padding: "18px 20px", marginBottom: 12 }, children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", alignItems: "center", gap: 12 }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx(
          "span",
          {
            className: "dot",
            style: {
              background: running ? "var(--success)" : "var(--text-tertiary)",
              animation: running ? "pulse 1.6s infinite" : void 0
            }
          }
        ),
        /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minWidth: 0 }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-strong truncate", children: session.status !== "" ? session.status : "当前没有运行中的游戏" }),
          /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap truncate", children: running ? `进程 pid ${session.pid}` : session.exitCode !== void 0 ? `上次退出码 ${session.exitCode}` : "在首页选择版本与账户后启动游戏" })
        ] }),
        running && /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn btn-danger", onClick: () => void killGame(), children: "结束游戏" }),
        hasLogs && /* @__PURE__ */ jsxRuntimeExports.jsxs("button", { className: "btn btn-subtle", onClick: session.clearLogs, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx(IconClose, { size: 13 }),
          " 清空日志"
        ] })
      ] }) }),
      /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card card-flush", style: { flex: 1, minHeight: 0 }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card-head", children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "span",
            {
              className: "dot",
              style: {
                background: running ? "var(--accent)" : "var(--surface-hover)",
                animation: running ? "pulse 1.6s infinite" : void 0
              }
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "card-title", children: "实时日志" }),
          /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { flex: 1 } }),
          /* @__PURE__ */ jsxRuntimeExports.jsxs("span", { className: "chip", children: [
            session.logs.length,
            " 行"
          ] })
        ] }),
        hasLogs ? /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "log", children: session.logs.map((line, index) => /* @__PURE__ */ jsxRuntimeExports.jsx("div", { children: line }, index)) }) : /* @__PURE__ */ jsxRuntimeExports.jsx(EmptyState, { title: "暂无日志", hint: "游戏启动后，这里会实时显示输出。" })
      ] })
    ] }),
    host
  ] });
}
function avatarUrl(a) {
  const id2 = a.type === "microsoft" ? a.uuid : a.name;
  return `https://minotar.net/helm/${encodeURIComponent(id2)}/128`;
}
const TYPE_LABEL = {
  microsoft: "微软账户",
  external: "外置账户",
  offline: "离线账户"
};
const ADD_KIND_LABEL = {
  microsoft: "微软账户",
  external: "外置账户",
  offline: "离线账户"
};
const MIN_POLL_INTERVAL_SEC = 3;
function AccountsPage() {
  const { push, host } = useToasts();
  const [accounts, setAccounts] = reactExports.useState([]);
  const [current, setCurrent] = reactExports.useState();
  const [loading, setLoading] = reactExports.useState(true);
  const [failedAvatars, setFailedAvatars] = reactExports.useState(/* @__PURE__ */ new Set());
  const [pendingRemove, setPendingRemove] = reactExports.useState(null);
  const [detailFor, setDetailFor] = reactExports.useState(null);
  const [addOpen, setAddOpen] = reactExports.useState(false);
  const [addKind, setAddKind] = reactExports.useState("microsoft");
  const [addBusy, setAddBusy] = reactExports.useState(false);
  const [addError, setAddError] = reactExports.useState("");
  const [device, setDevice] = reactExports.useState(null);
  const [msStatus, setMsStatus] = reactExports.useState("");
  const [codeCopied, setCodeCopied] = reactExports.useState(false);
  const addErrorRef = reactExports.useRef("");
  const closeAddRef = reactExports.useRef(() => void 0);
  const loadRef = reactExports.useRef(async () => void 0);
  const pushToastRef = reactExports.useRef(
    () => void 0
  );
  const generationRef = reactExports.useRef(0);
  const pollRef = reactExports.useRef(null);
  const [extUrl, setExtUrl] = reactExports.useState("");
  const [extName, setExtName] = reactExports.useState("");
  const [extPwd, setExtPwd] = reactExports.useState("");
  const [draftName, setDraftName] = reactExports.useState("");
  const load = reactExports.useCallback(async () => {
    setLoading(true);
    try {
      const data = await request("accounts.list");
      setAccounts(data.accounts);
      setCurrent(data.current);
    } catch (e) {
      push("error", `读取账户失败：${String(e)}`);
    }
    setLoading(false);
  }, [push]);
  reactExports.useEffect(() => {
    void load();
  }, [load]);
  reactExports.useEffect(
    () => () => {
      if (pollRef.current !== null) window.clearInterval(pollRef.current);
      pollRef.current = null;
    },
    []
  );
  const stopPolling = reactExports.useCallback(() => {
    if (pollRef.current !== null) window.clearInterval(pollRef.current);
    pollRef.current = null;
  }, []);
  const nameValid = /^[A-Za-z0-9_]{3,16}$/.test(draftName.trim());
  const extReady = extUrl.trim() !== "" && extName.trim() !== "" && extPwd !== "";
  function closeAdd() {
    stopPolling();
    generationRef.current += 1;
    setAddOpen(false);
    setAddBusy(false);
    setAddError("");
    addErrorRef.current = "";
    setDevice(null);
    setMsStatus("");
    setCodeCopied(false);
    setDraftName("");
    setExtUrl("");
    setExtName("");
    setExtPwd("");
  }
  function syncRefs() {
    closeAddRef.current = closeAdd;
    loadRef.current = load;
    pushToastRef.current = push;
  }
  syncRefs();
  const startPolling = reactExports.useCallback((session, generation) => {
    const intervalMs = Math.max(session.interval, MIN_POLL_INTERVAL_SEC) * 1e3;
    setMsStatus("等待授权…");
    const timer = window.setInterval(() => {
      void (async () => {
        if (generation !== generationRef.current) return;
        try {
          const r2 = await request("auth.microsoft.poll", {
            deviceCode: session.deviceCode
          });
          if (generation !== generationRef.current) return;
          if (r2.state === "success") {
            stopPolling();
            pushToastRef.current(
              "success",
              r2.account !== void 0 ? `Microsoft 账户已登录：${r2.account.name}` : "Microsoft 账户已登录"
            );
            closeAddRef.current();
            await loadRef.current();
            return;
          }
          setMsStatus(r2.message !== void 0 && r2.message !== "" ? r2.message : "等待授权…");
        } catch (e) {
          if (generation !== generationRef.current) return;
          stopPolling();
          setMsStatus("");
          const message = `登录失败：${String(e)}`;
          addErrorRef.current = message;
          setAddError(message);
        }
      })();
    }, intervalMs);
    pollRef.current = timer;
  }, [stopPolling]);
  async function startMicrosoft() {
    setAddBusy(true);
    setAddError("");
    try {
      const session = await request("auth.microsoft.start");
      const generation = generationRef.current + 1;
      generationRef.current = generation;
      setDevice(session);
      startPolling(session, generation);
    } catch (e) {
      setAddError(`获取设备码失败：${String(e)}`);
    } finally {
      setAddBusy(false);
    }
  }
  async function openVerification() {
    if (device === null) return;
    try {
      await navigator.clipboard?.writeText(device.userCode);
      setCodeCopied(true);
    } catch {
    }
    try {
      const api = nativeApi();
      if (api === null) {
        push("info", `请手动打开 ${device.verificationUri} 并输入代码 ${device.userCode}`);
        return;
      }
      await api.openExternal(device.verificationUri);
    } catch (e) {
      setAddError(`打开验证网站失败：${String(e)}`);
    }
  }
  async function loginExternal() {
    if (!extReady) return;
    setAddBusy(true);
    setAddError("");
    try {
      const r2 = await request("auth.yggdrasil.login", {
        url: extUrl.trim(),
        username: extName.trim(),
        password: extPwd
      });
      push("success", `第三方登录 ${r2.account.name} 已添加`);
      setExtPwd("");
      closeAdd();
      await load();
    } catch (e) {
      setAddError(`登录失败：${String(e)}`);
    } finally {
      setAddBusy(false);
    }
  }
  async function addOffline() {
    if (!nameValid) return;
    setAddBusy(true);
    setAddError("");
    try {
      await request("accounts.addOffline", { name: draftName.trim() });
      push("success", `离线账户 ${draftName.trim()} 已添加`);
      closeAdd();
      await load();
    } catch (e) {
      setAddError(`添加失败：${String(e)}`);
    } finally {
      setAddBusy(false);
    }
  }
  async function setAsCurrent(a) {
    try {
      await request("accounts.setCurrent", { uuid: a.uuid });
      push("success", `已将 ${a.name} 设为当前账户`);
      await load();
    } catch (e) {
      push("error", String(e));
    }
  }
  async function remove(a) {
    try {
      await request("accounts.remove", { uuid: a.uuid });
      push("success", `已移除账户 ${a.name}`);
      setPendingRemove(null);
      await load();
    } catch (e) {
      push("error", `移除失败：${String(e)}`);
    }
  }
  syncRefs();
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page", children: [
    /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page-body", style: { display: "flex", flexDirection: "column", gap: 18 }, children: [
      /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "card", style: { padding: "18px 22px" }, children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 14, alignItems: "center" }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx(
          "div",
          {
            style: {
              width: 44,
              height: 44,
              borderRadius: "50%",
              background: "var(--accent)",
              color: "var(--text-on-accent)",
              display: "flex",
              alignItems: "center",
              justifyContent: "center",
              fontWeight: 700,
              fontSize: 18,
              flexShrink: 0
            },
            children: "?"
          }
        ),
        /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minWidth: 0 }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-strong", children: "未登录 Starcloud ID" }),
          /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: "登录后可使用云端模型、邀请与时刻等在线能力" })
        ] }),
        /* @__PURE__ */ jsxRuntimeExports.jsx(
          "button",
          {
            className: "btn btn-primary",
            onClick: () => push("info", "Starcloud ID 登录：内核能力接入后可用"),
            children: "登录"
          }
        )
      ] }) }),
      /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { children: [
        /* @__PURE__ */ jsxRuntimeExports.jsxs(
          "div",
          {
            style: {
              display: "flex",
              alignItems: "center",
              gap: 10,
              marginBottom: 12
            },
            children: [
              /* @__PURE__ */ jsxRuntimeExports.jsx(
                "span",
                {
                  style: {
                    width: 4,
                    height: 18,
                    borderRadius: 2,
                    background: "var(--accent)",
                    flexShrink: 0
                  }
                }
              ),
              /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-strong", style: { fontSize: 16 }, children: "游戏账户" }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("span", { className: "t-cap", children: [
                accounts.length,
                " 个账户"
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { flex: 1 } }),
              /* @__PURE__ */ jsxRuntimeExports.jsx(
                "button",
                {
                  className: "btn btn-primary",
                  onClick: () => {
                    setAddOpen(true);
                    setAddError("");
                  },
                  children: "添加账户"
                }
              )
            ]
          }
        ),
        loading && /* @__PURE__ */ jsxRuntimeExports.jsx(SkeletonList, { rows: 2, avatar: true }),
        !loading && accounts.length === 0 && /* @__PURE__ */ jsxRuntimeExports.jsx(
          EmptyState,
          {
            title: "还没有游戏账户",
            hint: "点右上角「添加账户」用微软账户登录，或建一个离线账户。",
            action: /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn btn-primary", onClick: () => setAddOpen(true), children: "添加账户" })
          }
        ),
        accounts.map((a) => {
          const isCurrent = a.uuid === current || a.isDefault === true;
          const failed = failedAvatars.has(a.uuid);
          return /* @__PURE__ */ jsxRuntimeExports.jsxs(
            "div",
            {
              className: "row",
              style: { minHeight: 72 },
              onClick: () => setDetailFor(a),
              children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  "div",
                  {
                    className: "icon32",
                    style: { width: 48, height: 48, borderRadius: 8, overflow: "hidden" },
                    children: failed ? /* @__PURE__ */ jsxRuntimeExports.jsx("span", { style: { fontSize: 18 }, children: a.name[0]?.toUpperCase() }) : /* @__PURE__ */ jsxRuntimeExports.jsx(
                      "img",
                      {
                        src: avatarUrl(a),
                        alt: "",
                        width: 48,
                        height: 48,
                        style: { objectFit: "cover" },
                        onError: () => setFailedAvatars((prev) => new Set(prev).add(a.uuid))
                      }
                    )
                  }
                ),
                /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minWidth: 0 }, children: [
                  /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", alignItems: "center", gap: 8 }, children: [
                    /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-strong truncate", children: a.name }),
                    isCurrent && /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "chip chip-accent", children: "首选" }),
                    a.hasSession === false && /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "chip chip-danger", children: "需重新登录" })
                  ] }),
                  /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-cap", children: [
                    TYPE_LABEL[a.type] ?? a.type,
                    a.serverName !== void 0 && a.serverName !== "" ? ` · ${a.serverName}` : ""
                  ] })
                ] }),
                a.skinId !== void 0 && a.skinId !== "" && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-cap", style: { textAlign: "right" }, children: [
                  "皮肤",
                  /* @__PURE__ */ jsxRuntimeExports.jsx("br", {}),
                  a.skinId
                ] }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  "button",
                  {
                    className: "btn btn-subtle btn-sm btn-danger",
                    title: "移除账户",
                    onClick: (e) => {
                      e.stopPropagation();
                      setPendingRemove(a);
                    },
                    children: "移除"
                  }
                ),
                /* @__PURE__ */ jsxRuntimeExports.jsx(IconChevronRight, { size: 15, style: { opacity: 0.45 } })
              ]
            },
            a.uuid
          );
        })
      ] })
    ] }),
    /* @__PURE__ */ jsxRuntimeExports.jsx(
      Modal,
      {
        open: addOpen,
        title: "添加账户",
        onClose: closeAdd,
        width: 560,
        footer: addKind === "microsoft" ? /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: closeAdd, children: "取消" }),
          device === null ? /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn btn-primary",
              disabled: addBusy,
              onClick: () => void startMicrosoft(),
              children: addBusy ? "获取中…" : "开始登录"
            }
          ) : /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: closeAdd, children: "取消登录" })
        ] }) : /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: closeAdd, children: "取消" }),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn btn-primary",
              disabled: addKind === "offline" ? !nameValid || addBusy : !extReady || addBusy,
              onClick: () => {
                if (addKind === "offline") void addOffline();
                else void loginExternal();
              },
              children: addBusy ? "处理中…" : addKind === "offline" ? "添加" : "登录"
            }
          )
        ] }),
        children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", flexDirection: "column", gap: 14 }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            Segmented,
            {
              value: addKind,
              options: [
                { value: "microsoft", label: ADD_KIND_LABEL.microsoft },
                { value: "external", label: ADD_KIND_LABEL.external },
                { value: "offline", label: ADD_KIND_LABEL.offline }
              ],
              onChange: (v2) => {
                setAddKind(v2);
                setAddError("");
              }
            }
          ),
          addError !== "" && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "info info-danger", children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx(IconClose, { size: 14 }),
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "info-body", children: addError })
          ] }),
          addKind === "microsoft" && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { display: "flex", flexDirection: "column", gap: 12 }, children: device === null ? /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-body", children: "使用一次性验证代码登录 Microsoft 账户：点「开始登录」拿到代码后， 用浏览器打开验证网站输入代码完成授权，启动器会自动把账户加进来。" }) : /* @__PURE__ */ jsxRuntimeExports.jsxs(
            "div",
            {
              style: {
                display: "flex",
                flexDirection: "column",
                alignItems: "center",
                gap: 10,
                padding: "6px 0"
              },
              children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-body", style: { textAlign: "center" }, children: "使用一次性验证代码登录到你的 Microsoft 账户" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  "div",
                  {
                    className: "t-mono",
                    style: {
                      fontSize: 30,
                      fontWeight: 600,
                      letterSpacing: 3,
                      color: "var(--text-primary)",
                      userSelect: "text"
                    },
                    children: device.userCode
                  }
                ),
                /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-cap", style: { textAlign: "center" }, children: [
                  "使用浏览器打开 ",
                  device.verificationUri,
                  "，输入以上代码并按照网页提示完成授权。 完成后启动器将自动把账户添加至启动器内。"
                ] }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  "button",
                  {
                    className: "btn btn-primary",
                    style: { width: "100%" },
                    onClick: () => void openVerification(),
                    children: codeCopied ? "已复制并打开验证网站" : "复制并打开验证网站"
                  }
                ),
                /* @__PURE__ */ jsxRuntimeExports.jsxs(
                  "div",
                  {
                    style: { display: "flex", alignItems: "center", gap: 8, color: "var(--text-secondary)" },
                    children: [
                      /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "spinner" }),
                      /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-cap", children: msStatus === "" ? "等待授权…" : msStatus })
                    ]
                  }
                )
              ]
            }
          ) }),
          addKind === "external" && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", flexDirection: "column", gap: 10 }, children: [
            /* @__PURE__ */ jsxRuntimeExports.jsxs("label", { style: { display: "flex", flexDirection: "column", gap: 4 }, children: [
              /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-cap", children: "认证服务器地址" }),
              /* @__PURE__ */ jsxRuntimeExports.jsx(
                "input",
                {
                  className: "input",
                  value: extUrl,
                  placeholder: "例如 https://littleskin.cn/api/yggdrasil",
                  onChange: (e) => setExtUrl(e.target.value)
                }
              )
            ] }),
            /* @__PURE__ */ jsxRuntimeExports.jsxs("label", { style: { display: "flex", flexDirection: "column", gap: 4 }, children: [
              /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-cap", children: "邮箱或用户名" }),
              /* @__PURE__ */ jsxRuntimeExports.jsx(
                "input",
                {
                  className: "input",
                  autoFocus: true,
                  value: extName,
                  onChange: (e) => setExtName(e.target.value)
                }
              )
            ] }),
            /* @__PURE__ */ jsxRuntimeExports.jsxs("label", { style: { display: "flex", flexDirection: "column", gap: 4 }, children: [
              /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-cap", children: "密码" }),
              /* @__PURE__ */ jsxRuntimeExports.jsx(
                "input",
                {
                  className: "input",
                  type: "password",
                  value: extPwd,
                  onChange: (e) => setExtPwd(e.target.value),
                  onKeyDown: (e) => {
                    if (e.key === "Enter" && extReady && !addBusy) void loginExternal();
                  }
                }
              )
            ] })
          ] }),
          addKind === "offline" && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", flexDirection: "column", gap: 8 }, children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-cap", children: "玩家名（3–16 位字母、数字或下划线）" }),
            /* @__PURE__ */ jsxRuntimeExports.jsx(
              "input",
              {
                className: "input",
                autoFocus: true,
                value: draftName,
                placeholder: "例如 Steve",
                onChange: (e) => setDraftName(e.target.value),
                onKeyDown: (e) => {
                  if (e.key === "Enter" && nameValid) void addOffline();
                }
              }
            ),
            draftName.trim() !== "" && !nameValid ? /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-cap", style: { color: "var(--danger)" }, children: "名字不合法：3–16 位字母/数字/下划线" }) : /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-cap", children: "离线账户无需登录，可用于单人游戏、局域网联机与允许离线登录的服务器。" })
          ] })
        ] })
      }
    ),
    /* @__PURE__ */ jsxRuntimeExports.jsx(
      Modal,
      {
        open: pendingRemove !== null,
        title: "移除账户？",
        onClose: () => setPendingRemove(null),
        footer: /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: () => setPendingRemove(null), children: "取消" }),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn btn-primary",
              style: { background: "var(--danger)", color: "var(--text-on-accent)" },
              onClick: () => {
                const a = pendingRemove;
                if (a !== null) void remove(a);
              },
              children: "移除"
            }
          )
        ] }),
        children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "dialog-body", children: [
          "将从启动器移除账户 ",
          /* @__PURE__ */ jsxRuntimeExports.jsx("b", { children: pendingRemove?.name }),
          "（不会删除游戏内的玩家数据）。"
        ] })
      }
    ),
    /* @__PURE__ */ jsxRuntimeExports.jsx(
      Modal,
      {
        open: detailFor !== null,
        title: detailFor?.name ?? "",
        width: 480,
        onClose: () => setDetailFor(null),
        footer: /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: () => setDetailFor(null), children: "返回账户列表" }),
          detailFor !== null && detailFor.uuid !== current && /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn btn-primary",
              onClick: () => {
                const a = detailFor;
                setDetailFor(null);
                if (a !== null) void setAsCurrent(a);
              },
              children: "设为首选"
            }
          )
        ] }),
        children: detailFor !== null && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", flexDirection: "column", gap: 14 }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 14, alignItems: "center" }, children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx(
              "div",
              {
                className: "icon32",
                style: { width: 72, height: 72, borderRadius: 8, overflow: "hidden" },
                children: failedAvatars.has(detailFor.uuid) ? /* @__PURE__ */ jsxRuntimeExports.jsx("span", { style: { fontSize: 28 }, children: detailFor.name[0]?.toUpperCase() }) : /* @__PURE__ */ jsxRuntimeExports.jsx(
                  "img",
                  {
                    src: avatarUrl(detailFor),
                    alt: "",
                    width: 72,
                    height: 72,
                    style: { objectFit: "cover" },
                    onError: () => setFailedAvatars((prev) => new Set(prev).add(detailFor.uuid))
                  }
                )
              }
            ),
            /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minWidth: 0 }, children: [
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", alignItems: "center", gap: 8 }, children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx("span", { style: { fontSize: 17, fontWeight: 600 }, className: "truncate", children: detailFor.name }),
                detailFor.uuid === current && /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "chip chip-accent", children: "首选" })
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-cap", children: [
                TYPE_LABEL[detailFor.type] ?? detailFor.type,
                detailFor.serverName !== void 0 && detailFor.serverName !== "" ? ` · ${detailFor.serverName}` : ""
              ] })
            ] })
          ] }),
          /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", flexDirection: "column", gap: 6 }, children: [
            /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-cap", children: [
              "UUID：",
              /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-mono", children: detailFor.uuid })
            ] }),
            detailFor.skinId !== void 0 && detailFor.skinId !== "" && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-cap", children: [
              "皮肤：",
              /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-mono", children: detailFor.skinId })
            ] })
          ] }),
          /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 8, flexWrap: "wrap" }, children: [
            (detailFor.type === "microsoft" || detailFor.type === "external") && /* @__PURE__ */ jsxRuntimeExports.jsx(
              "button",
              {
                className: "btn",
                onClick: () => {
                  setDetailFor(null);
                  setAddKind(detailFor.type === "microsoft" ? "microsoft" : "external");
                  setAddOpen(true);
                },
                children: "重新登录该账号"
              }
            ),
            /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: () => setDetailFor(null), children: "前往换肤" }),
            /* @__PURE__ */ jsxRuntimeExports.jsx(
              "button",
              {
                className: "btn btn-danger",
                onClick: () => {
                  const a = detailFor;
                  setDetailFor(null);
                  if (a !== null) setPendingRemove(a);
                },
                children: "删除账户"
              }
            )
          ] }),
          /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: "删除只移除本机保存的身份信息，不动游戏存档与版本文件。" })
        ] })
      }
    ),
    host
  ] });
}
function textOf(node, bindings) {
  if (node.value === void 0 || node.value === null) return "";
  if (node.value.bind !== void 0 && node.value.bind !== null) {
    const key = `${node.value.bind.scope}.${node.value.bind.key}`;
    const v2 = bindings[key];
    return v2 === void 0 || v2 === null ? "" : String(v2);
  }
  return node.value.text;
}
function PluginViewNodeView(props) {
  const { node, bindings, pluginId } = props;
  const [busy, setBusy] = reactExports.useState(false);
  async function run(action) {
    if (action === void 0 || action === null) return;
    const api = action.handler.length > 0 ? action.handler : action.api;
    if (api === "") return;
    let args = [];
    try {
      args = JSON.parse(action.argsJson === "" ? "[]" : action.argsJson);
    } catch {
      args = [];
    }
    setBusy(true);
    try {
      const r2 = await request("plugins.invoke", {
        id: pluginId,
        api,
        args
      });
      if (!r2.ok) props.onError(r2.error ?? "动作执行失败");
      props.onRefresh();
    } catch (e) {
      props.onError(String(e));
    } finally {
      setBusy(false);
    }
  }
  const children = (node.children ?? []).map((child, i) => /* @__PURE__ */ jsxRuntimeExports.jsx(
    PluginViewNodeView,
    {
      node: child,
      bindings,
      pluginId,
      onRefresh: props.onRefresh,
      onError: props.onError
    },
    `${child.type}-${i}`
  ));
  switch (node.type) {
    case "stack":
      return /* @__PURE__ */ jsxRuntimeExports.jsx(
        "div",
        {
          style: {
            display: "flex",
            flexDirection: node.direction === "row" ? "row" : "column",
            gap: node.spacing !== void 0 && node.spacing > 0 ? node.spacing : 8,
            alignItems: node.direction === "row" ? "center" : "stretch",
            flexWrap: "wrap"
          },
          children
        }
      );
    case "text":
      return /* @__PURE__ */ jsxRuntimeExports.jsx(
        "div",
        {
          className: "t-body",
          style: { whiteSpace: node.wrap === false ? "nowrap" : "pre-wrap" },
          children: textOf(node, bindings)
        }
      );
    case "title":
      return /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-strong", children: textOf(node, bindings) });
    case "caption":
      return /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: textOf(node, bindings) });
    case "button":
      return /* @__PURE__ */ jsxRuntimeExports.jsx(
        "button",
        {
          className: `btn${node.kind === "accent" ? " btn-primary" : ""}${node.kind === "danger" ? " btn-danger" : ""}`,
          disabled: busy,
          onClick: () => void run(node.action),
          children: textOf(node, bindings)
        }
      );
    case "toggle": {
      const key = node.bind !== void 0 && node.bind !== null ? `${node.bind.scope}.${node.bind.key}` : "";
      const checked = bindings[key] === true || bindings[key] === "true";
      return /* @__PURE__ */ jsxRuntimeExports.jsxs("label", { style: { display: "flex", alignItems: "center", gap: 10 }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx(
          "button",
          {
            className: "toggle",
            role: "switch",
            "aria-checked": checked,
            disabled: busy,
            onClick: () => void run(node.action)
          }
        ),
        /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-body", children: textOf(node, bindings) })
      ] });
    }
    case "input": {
      const key = node.bind !== void 0 && node.bind !== null ? `${node.bind.scope}.${node.bind.key}` : "";
      const value = bindings[key];
      return /* @__PURE__ */ jsxRuntimeExports.jsxs("label", { style: { display: "flex", flexDirection: "column", gap: 4 }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-cap", children: textOf(node, bindings) }),
        node.multiline === true ? /* @__PURE__ */ jsxRuntimeExports.jsx(
          "textarea",
          {
            className: "input",
            style: { minHeight: 72 },
            placeholder: node.placeholder,
            defaultValue: value === void 0 || value === null ? "" : String(value)
          }
        ) : /* @__PURE__ */ jsxRuntimeExports.jsx(
          "input",
          {
            className: "input",
            placeholder: node.placeholder,
            defaultValue: value === void 0 || value === null ? "" : String(value)
          }
        )
      ] });
    }
    case "select": {
      const key = node.bind !== void 0 && node.bind !== null ? `${node.bind.scope}.${node.bind.key}` : "";
      const value = bindings[key];
      return /* @__PURE__ */ jsxRuntimeExports.jsxs("label", { style: { display: "flex", flexDirection: "column", gap: 4 }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-cap", children: textOf(node, bindings) }),
        /* @__PURE__ */ jsxRuntimeExports.jsx("select", { className: "input", defaultValue: value === void 0 || value === null ? "" : String(value), children: (node.options ?? []).map((opt) => /* @__PURE__ */ jsxRuntimeExports.jsx("option", { value: opt, children: opt }, opt)) })
      ] });
    }
    case "list": {
      const key = node.itemsBind !== void 0 && node.itemsBind !== null ? `${node.itemsBind.scope}.${node.itemsBind.key}` : "";
      const raw = bindings[key];
      const items = Array.isArray(raw) ? raw : [];
      if (items.length === 0) {
        return /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: "（空）" });
      }
      return /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { display: "flex", flexDirection: "column", gap: 6 }, children: items.map((item, i) => /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "row row-static", style: { minHeight: 0 }, children: /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-body", children: typeof item === "string" ? item : JSON.stringify(item) }) }, i)) });
    }
    case "table": {
      const key = node.itemsBind !== void 0 && node.itemsBind !== null ? `${node.itemsBind.scope}.${node.itemsBind.key}` : "";
      const raw = bindings[key];
      const rows = Array.isArray(raw) ? raw : [];
      const cols = node.columns ?? [];
      return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", flexDirection: "column", gap: 2 }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { display: "flex", gap: 12 }, children: cols.map((c) => /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-cap", style: { flex: 1 }, children: c }, c)) }),
        rows.map((row, i) => /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { display: "flex", gap: 12 }, children: cols.map((c) => /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-body", style: { flex: 1 }, children: typeof row === "object" && row !== null ? String(row[c] ?? "") : "" }, c)) }, i))
      ] });
    }
    case "image":
      return node.source === "" ? /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: "（图片）" }) : /* @__PURE__ */ jsxRuntimeExports.jsx(
        "img",
        {
          src: node.source,
          alt: "",
          style: { maxWidth: "100%", borderRadius: "var(--r-sm)" }
        }
      );
    case "progress":
      return /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "progress", style: { width: "100%" }, children: /* @__PURE__ */ jsxRuntimeExports.jsx(
        "div",
        {
          className: "progress-fill",
          style: { width: `${Math.max(0, Math.min(100, node.progress ?? 0))}%` }
        }
      ) });
    case "badge":
      return /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "chip chip-accent", children: textOf(node, bindings) });
    case "divider":
      return /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { height: 1, background: "var(--stroke-divider)" } });
    case "link":
      return /* @__PURE__ */ jsxRuntimeExports.jsx(
        "a",
        {
          className: "t-body",
          style: { color: "var(--accent)" },
          href: node.href,
          onClick: (e) => {
            e.preventDefault();
            void run(node.action);
          },
          children: textOf(node, bindings)
        }
      );
    case "code":
      return /* @__PURE__ */ jsxRuntimeExports.jsx(
        "pre",
        {
          className: "log",
          style: {
            margin: 0,
            padding: 10,
            background: "var(--surface-subtle)",
            borderRadius: "var(--r-sm)",
            maxHeight: (node.height ?? 0) > 0 ? node.height : 240
          },
          children: textOf(node, bindings)
        }
      );
    default:
      return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-cap", children: [
        "未支持的组件：",
        node.type
      ] });
  }
}
function PluginPanelView(props) {
  const [result, setResult] = reactExports.useState(null);
  const [loading, setLoading] = reactExports.useState(true);
  const [tick, setTick] = reactExports.useState(0);
  reactExports.useEffect(() => {
    let alive = true;
    setLoading(true);
    void request("plugins.view", {
      id: props.pluginId,
      panelId: props.panelId
    }).then((r2) => {
      if (alive) setResult(r2);
    }).catch((e) => {
      if (alive) props.onError(String(e));
    }).finally(() => {
      if (alive) setLoading(false);
    });
    return () => {
      alive = false;
    };
  }, [props.pluginId, props.panelId, tick]);
  if (loading) return /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: "加载面板…" });
  if (result === null) return /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: "面板加载失败" });
  if (!result.ok) {
    return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "info info-danger", children: [
      /* @__PURE__ */ jsxRuntimeExports.jsx(IconClose, { size: 14 }),
      /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "info-body", children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-strong", children: "面板定义不合法" }),
        (result.errors ?? []).map((e) => /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: e }, e))
      ] })
    ] });
  }
  const view = result.view ?? null;
  if (view === null) return /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: "面板没有内容" });
  return /* @__PURE__ */ jsxRuntimeExports.jsx(
    PluginViewNodeView,
    {
      node: view,
      bindings: result.bindings ?? {},
      pluginId: props.pluginId,
      onRefresh: () => setTick((n2) => n2 + 1),
      onError: props.onError
    }
  );
}
const CORE_PLUGINS = [
  {
    kind: "引擎",
    name: "图标缓存",
    version: "1.0.0",
    desc: "把远程图标/封面落到本地磁盘并复用（走下载引擎）",
    provides: "IIconCache"
  },
  {
    kind: "引擎",
    name: "多线程下载引擎",
    version: "1.0.0",
    desc: "多文件批量下载 + 多源分片竞速 + 重试 + 断点续传；可在局域网内复用同账号设备的已下载文件",
    provides: "IDownloadEngine"
  },
  {
    kind: "引擎",
    name: "搜索词翻译",
    version: "1.0.0",
    desc: "中文搜索词 → 英文项目名（内置词典 → MC 百科 → 机器翻译，带缓存）",
    provides: "ITranslationEngine"
  },
  {
    kind: "功能",
    name: "投影管理",
    version: "1.0.0",
    desc: "扫描全局与各版本实例的投影文件，可把投影复制/移动到指定版本",
    provides: "IProjectionLibrary"
  },
  {
    kind: "功能",
    name: "版本管理",
    version: "1.0.0",
    desc: "官方版本清单拉取与缓存、本地已安装版本枚举、版本删除",
    provides: "IVersionCatalog"
  }
];
function PluginsPage() {
  const { push, host } = useToasts();
  const [plugins, setPlugins] = reactExports.useState([]);
  const [loading, setLoading] = reactExports.useState(true);
  const [installOpen, setInstallOpen] = reactExports.useState(false);
  const [installPath, setInstallPath] = reactExports.useState("");
  const [busy, setBusy] = reactExports.useState(null);
  const [pendingUninstall, setPendingUninstall] = reactExports.useState(null);
  const [pendingDisclaimer, setPendingDisclaimer] = reactExports.useState(null);
  const [panelFor, setPanelFor] = reactExports.useState(null);
  const [panels, setPanels] = reactExports.useState([]);
  const [activePanelId, setActivePanelId] = reactExports.useState("");
  const [panelBusy, setPanelBusy] = reactExports.useState(false);
  const load = reactExports.useCallback(async () => {
    setLoading(true);
    try {
      const r2 = await request("plugins.list");
      setPlugins(r2.plugins);
    } catch (e) {
      push("error", `读取插件失败：${String(e)}`);
    }
    setLoading(false);
  }, [push]);
  reactExports.useEffect(() => {
    void load();
  }, [load]);
  async function install() {
    if (installPath.trim() === "") return;
    setBusy("__install__");
    try {
      const p2 = await request("plugins.install", {
        path: installPath.trim()
      });
      push("success", `已安装插件 ${p2.name} ${p2.version}`);
      setInstallOpen(false);
      setInstallPath("");
      await load();
    } catch (e) {
      push("error", `安装失败：${String(e)}`);
    } finally {
      setBusy(null);
    }
  }
  async function togglePlugin(p2, enabled) {
    if (enabled && !p2.official && !p2.disclaimerAccepted) {
      setPendingDisclaimer(p2);
      return;
    }
    setBusy(p2.id);
    try {
      await request("plugins.setEnabled", { id: p2.id, enabled });
      await load();
    } catch (e) {
      push("error", String(e));
    } finally {
      setBusy(null);
    }
  }
  async function uninstall(p2) {
    setBusy(p2.id);
    try {
      await request("plugins.uninstall", { id: p2.id });
      push("success", `已卸载 ${p2.name}`);
      setPendingUninstall(null);
      await load();
    } catch (e) {
      push("error", String(e));
    } finally {
      setBusy(null);
    }
  }
  async function openPanels(p2) {
    setPanelFor(p2);
    setPanelBusy(true);
    try {
      const r2 = await request(
        "plugins.panels",
        { id: p2.id }
      );
      setPanels(r2.panels);
      setActivePanelId(r2.panels[0]?.id ?? "");
    } catch (e) {
      push("error", String(e));
      setPanelFor(null);
    } finally {
      setPanelBusy(false);
    }
  }
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page", children: [
    /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page-head", children: [
      /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { flex: 1 } }),
      /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn btn-primary", onClick: () => setInstallOpen(true), children: "安装插件…" })
    ] }),
    /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page-body", style: { display: "flex", flexDirection: "column", gap: 14 }, children: [
      /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", children: [
        /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", alignItems: "baseline", gap: 10, marginBottom: 6 }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-strong", style: { fontSize: 15 }, children: "内核插件" }),
          /* @__PURE__ */ jsxRuntimeExports.jsxs("span", { className: "t-cap", children: [
            "共 ",
            CORE_PLUGINS.length,
            " 个 · 内核 1.0.0"
          ] })
        ] }),
        CORE_PLUGINS.map((p2) => /* @__PURE__ */ jsxRuntimeExports.jsxs(
          "div",
          {
            className: "row row-static",
            style: { alignItems: "flex-start", minHeight: 0, paddingTop: 10, paddingBottom: 10 },
            children: [
              /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "chip", style: { marginTop: 2 }, children: p2.kind }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minWidth: 0 }, children: [
                /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", alignItems: "baseline", gap: 8 }, children: [
                  /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-strong", children: p2.name }),
                  /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-meta", children: p2.version })
                ] }),
                /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-cap", children: [
                  p2.desc,
                  "　提供：",
                  p2.provides
                ] })
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-cap", style: { flexShrink: 0 }, children: "运行中" })
            ]
          },
          p2.name
        ))
      ] }),
      /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-strong", style: { fontSize: 15, marginBottom: 6 }, children: "已安装" }),
        loading && /* @__PURE__ */ jsxRuntimeExports.jsx(SkeletonList, { rows: 2 }),
        !loading && plugins.length === 0 && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", style: { padding: "12px 0" }, children: "还没有安装第三方插件。" }),
        plugins.map((p2) => /* @__PURE__ */ jsxRuntimeExports.jsxs(
          "div",
          {
            className: "row row-static",
            style: { alignItems: "flex-start", minHeight: 0, paddingTop: 12, paddingBottom: 12 },
            children: [
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minWidth: 0 }, children: [
                /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", alignItems: "center", gap: 8, flexWrap: "wrap" }, children: [
                  /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-strong", children: p2.name }),
                  /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-meta", children: p2.version }),
                  !p2.official && /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "chip chip-accent", children: "第三方未验证" }),
                  /* @__PURE__ */ jsxRuntimeExports.jsxs("span", { className: "chip", children: [
                    "API ",
                    p2.apiVersion
                  ] })
                ] }),
                /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: p2.description }),
                /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-meta", style: { opacity: 0.8 }, children: [
                  "作者 ",
                  p2.author === "" ? "未知" : p2.author,
                  p2.permissions.length > 0 && ` · 能力：${p2.permissions.join(" · ")}`
                ] })
              ] }),
              p2.panelCount > 0 && /* @__PURE__ */ jsxRuntimeExports.jsx(
                "button",
                {
                  className: "btn btn-sm",
                  disabled: busy !== null || panelBusy,
                  onClick: () => void openPanels(p2),
                  children: "打开面板"
                }
              ),
              /* @__PURE__ */ jsxRuntimeExports.jsx(
                "button",
                {
                  className: "btn btn-sm",
                  disabled: busy !== null,
                  onClick: () => setPendingUninstall(p2),
                  children: "卸载"
                }
              ),
              /* @__PURE__ */ jsxRuntimeExports.jsx(
                Toggle,
                {
                  checked: p2.enabled,
                  disabled: busy !== null,
                  label: `启用 ${p2.name}`,
                  onChange: (v2) => void togglePlugin(p2, v2)
                }
              )
            ]
          },
          p2.id
        ))
      ] }),
      /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: "把 .mclplug 文件放到这里：点「安装插件…」选择插件包，安装后打开开关启用。" })
    ] }),
    /* @__PURE__ */ jsxRuntimeExports.jsx(
      Modal,
      {
        open: installOpen,
        title: "安装插件",
        onClose: () => setInstallOpen(false),
        footer: /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: () => setInstallOpen(false), children: "取消" }),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn btn-primary",
              disabled: installPath.trim() === "" || busy !== null,
              onClick: () => void install(),
              children: "安装"
            }
          )
        ] }),
        children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", flexDirection: "column", gap: 8 }, children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-cap", children: "插件包路径（.mclplug 或已解压目录）" }),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "input",
            {
              className: "input",
              autoFocus: true,
              value: installPath,
              placeholder: "例如 /home/user/Downloads/demo.mclplug",
              onChange: (e) => setInstallPath(e.target.value)
            }
          )
        ] })
      }
    ),
    /* @__PURE__ */ jsxRuntimeExports.jsx(
      Modal,
      {
        open: pendingDisclaimer !== null,
        title: "启用第三方插件？",
        onClose: () => setPendingDisclaimer(null),
        footer: /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: () => setPendingDisclaimer(null), children: "取消" }),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn btn-primary",
              onClick: () => {
                const p2 = pendingDisclaimer;
                setPendingDisclaimer(null);
                if (p2 === null) return;
                void request("plugins.setEnabled", {
                  id: p2.id,
                  enabled: true,
                  acceptDisclaimer: true
                }).then(() => load()).catch((e) => push("error", String(e)));
              },
              children: "我了解风险，启用"
            }
          )
        ] }),
        children: /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "dialog-body", children: "该插件不是 Starcloud 官方发布，未经审核。插件可以访问它声明过的能力， 请确认来源可信后再启用。" })
      }
    ),
    /* @__PURE__ */ jsxRuntimeExports.jsx(
      Modal,
      {
        open: pendingUninstall !== null,
        title: "卸载插件？",
        onClose: () => setPendingUninstall(null),
        footer: /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: () => setPendingUninstall(null), children: "取消" }),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn btn-primary",
              style: { background: "var(--danger)", color: "#fff" },
              onClick: () => {
                const p2 = pendingUninstall;
                if (p2 !== null) void uninstall(p2);
              },
              children: "卸载"
            }
          )
        ] }),
        children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "dialog-body", children: [
          "将删除插件 ",
          /* @__PURE__ */ jsxRuntimeExports.jsx("b", { children: pendingUninstall?.name }),
          " 的目录与配置。"
        ] })
      }
    ),
    /* @__PURE__ */ jsxRuntimeExports.jsx(
      Modal,
      {
        open: panelFor !== null,
        title: panelFor === null ? "" : `${panelFor.name} · 面板`,
        width: 720,
        onClose: () => setPanelFor(null),
        footer: /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: () => setPanelFor(null), children: "关闭" }),
        children: panelBusy ? /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: "加载面板列表…" }) : panels.length === 0 ? /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: "该插件没有声明面板。" }) : /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 12, minHeight: 240 }, children: [
          panels.length > 1 && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { width: 160, flexShrink: 0, display: "flex", flexDirection: "column", gap: 2 }, children: panels.map((panel) => /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: `nav-item${activePanelId === panel.id ? " nav-active" : ""}`,
              style: { height: 36, width: "100%" },
              onClick: () => setActivePanelId(panel.id),
              children: panel.title
            },
            panel.id
          )) }),
          /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { flex: 1, minWidth: 0, overflowY: "auto" }, children: panels.find((x2) => x2.id === activePanelId)?.declarative === false ? /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "info info-warning", children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx(IconWarning, { size: 14 }),
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "info-body", children: "HTML 面板需要主进程文件 IPC，当前版本暂以声明式面板为准。" })
          ] }) : panelFor !== null && activePanelId !== "" ? /* @__PURE__ */ jsxRuntimeExports.jsx(
            PluginPanelView,
            {
              pluginId: panelFor.id,
              panelId: activePanelId,
              onError: (msg) => push("error", msg)
            }
          ) : null })
        ] })
      }
    ),
    host
  ] });
}
function SectionCard(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", children: [
    /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", alignItems: "center", gap: 10, marginBottom: 10 }, children: [
      /* @__PURE__ */ jsxRuntimeExports.jsx(
        "span",
        {
          style: { width: 4, height: 16, borderRadius: 2, background: "var(--accent)", flexShrink: 0 }
        }
      ),
      /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-strong", style: { fontSize: 15 }, children: props.title }),
      /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { flex: 1 } }),
      props.action
    ] }),
    props.children
  ] });
}
function NovaPage() {
  const { push, host } = useToasts();
  const [config, setConfig] = reactExports.useState({ baseUrl: "", apiKey: "", model: "" });
  const [draft, setDraft] = reactExports.useState({ baseUrl: "", apiKey: "", model: "" });
  const [useCloud, setUseCloud] = reactExports.useState(false);
  const [overlay, setOverlay] = reactExports.useState(false);
  const [chatOpen, setChatOpen] = reactExports.useState(false);
  const [plan, setPlan] = reactExports.useState(null);
  const [planBusy, setPlanBusy] = reactExports.useState(false);
  const [messages, setMessages] = reactExports.useState([]);
  const [input, setInput] = reactExports.useState("");
  const [busy, setBusy] = reactExports.useState(false);
  const seq = reactExports.useRef(1);
  const streamingId = reactExports.useRef(null);
  const bodyRef = reactExports.useRef(null);
  const loadConfig = reactExports.useCallback(async () => {
    try {
      const value = await request("nova.config.get");
      setConfig(value);
      setDraft(value);
    } catch (e) {
      push("error", `读取模型配置失败：${String(e)}`);
    }
  }, [push]);
  reactExports.useEffect(() => {
    void loadConfig();
  }, [loadConfig]);
  const configured = config.baseUrl.length > 0 && config.model.length > 0;
  async function saveConfig() {
    try {
      const value = await request("nova.config.set", {
        baseUrl: draft.baseUrl.trim(),
        apiKey: draft.apiKey.trim(),
        model: draft.model.trim()
      });
      setConfig(value);
      const ok2 = value.baseUrl.length > 0 && value.model.length > 0;
      push(ok2 ? "success" : "info", ok2 ? "模型配置已保存。" : "已清除模型配置。");
    } catch (e) {
      push("error", String(e));
    }
  }
  reactExports.useEffect(() => {
    onBridgeEvent((event, params) => {
      if (event === "nova.delta") {
        const text = String(params.text ?? "");
        if (text === "") return;
        const id2 = streamingId.current;
        setMessages(
          (prev) => prev.map((m2) => m2.id === id2 ? { ...m2, content: m2.content + text, streaming: true } : m2)
        );
      } else if (event === "nova.tool") {
        const tool = String(params.tool ?? "");
        const id2 = streamingId.current;
        setMessages((prev) => prev.map((m2) => m2.id === id2 ? { ...m2, tool } : m2));
      } else if (event === "nova.done") {
        const text = String(params.text ?? "");
        const planItems = params.plan;
        if (planItems !== void 0 && planItems.length > 0 && params.planId != null) {
          setPlan({ planId: String(params.planId), items: planItems, title: params.planTitle != null ? String(params.planTitle) : void 0 });
        }
        const id2 = streamingId.current;
        streamingId.current = null;
        setBusy(false);
        setMessages(
          (prev) => prev.map(
            (m2) => m2.id === id2 ? { ...m2, content: text !== "" ? text : m2.content, streaming: false, plan: planItems } : m2
          )
        );
      } else if (event === "nova.failed") {
        const error = String(params.error ?? "未知错误");
        const id2 = streamingId.current;
        streamingId.current = null;
        setBusy(false);
        setMessages(
          (prev) => prev.map(
            (m2) => m2.id === id2 ? { ...m2, content: `请求失败：${error}`, streaming: false } : m2
          )
        );
      }
    });
  }, []);
  reactExports.useEffect(() => {
    if (bodyRef.current !== null) {
      bodyRef.current.scrollTop = bodyRef.current.scrollHeight;
    }
  }, [messages]);
  async function send() {
    const text = input.trim();
    if (text === "" || busy) return;
    if (!configured) {
      push("info", "先在「模型设置」里填好 Base URL 与模型名");
      return;
    }
    setInput("");
    setBusy(true);
    const userId = seq.current++;
    const assistantId = seq.current++;
    streamingId.current = assistantId;
    setMessages((prev) => [
      ...prev,
      { id: userId, role: "user", content: text },
      { id: assistantId, role: "assistant", content: "", streaming: true }
    ]);
    try {
      await request("nova.chat", { message: text });
    } catch (e) {
      streamingId.current = null;
      setBusy(false);
      setMessages(
        (prev) => prev.map(
          (m2) => m2.id === assistantId ? { ...m2, content: `请求失败：${String(e)}`, streaming: false } : m2
        )
      );
    }
  }
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page", children: [
    /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page-body", style: { display: "flex", flexDirection: "column", gap: 14 }, children: [
      /* @__PURE__ */ jsxRuntimeExports.jsxs(
        SectionCard,
        {
          title: "Nova 账户",
          action: /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn btn-sm",
              onClick: () => push("info", "Starcloud ID 登录：内核能力接入后可用"),
              children: "登录"
            }
          ),
          children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", style: { marginBottom: 10 }, children: "登录后可用云端模型与邀请、时刻等在线能力；凭据只保存在本机。" }),
            /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", alignItems: "center", gap: 10 }, children: [
              /* @__PURE__ */ jsxRuntimeExports.jsx(IconSparkle, { size: 18, style: { color: "var(--accent)" } }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1 }, children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-body", children: "未登录" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-meta", children: "当前使用下面接入的本地模型" })
              ] })
            ] })
          ]
        }
      ),
      /* @__PURE__ */ jsxRuntimeExports.jsxs(
        SectionCard,
        {
          title: "云端模型",
          action: /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn btn-sm", onClick: () => void loadConfig(), children: "刷新" }),
          children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", style: { marginBottom: 10 }, children: "登录 Starcloud ID 后 Nova 默认走云端模型，不用自己填 Key；用量记在账号上。" }),
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-body", style: { marginBottom: 6 }, children: "使用云端模型" }),
            /* @__PURE__ */ jsxRuntimeExports.jsx(
              Toggle,
              {
                checked: useCloud,
                disabled: true,
                label: "使用云端模型",
                onChange: setUseCloud
              }
            ),
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", style: { marginTop: 10 }, children: "未登录时不可用；关掉后 Nova 只用下面接入的模型。" })
          ]
        }
      ),
      /* @__PURE__ */ jsxRuntimeExports.jsxs(
        SectionCard,
        {
          title: "模型设置",
          action: /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn btn-sm", onClick: () => void saveConfig(), children: "保存" }),
          children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", style: { marginBottom: 12 }, children: "接入任意 OpenAI 兼容服务：填 Base URL、Key 与模型名。Key 只保存在本机。" }),
            configured && /* @__PURE__ */ jsxRuntimeExports.jsx(
              "div",
              {
                className: "row row-static",
                style: { background: "var(--surface-subtle)", marginBottom: 12 },
                children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minWidth: 0 }, children: [
                  /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", alignItems: "center", gap: 8 }, children: [
                    /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-strong truncate", children: config.model }),
                    /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "chip chip-accent", children: "当前使用" })
                  ] }),
                  /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-meta truncate", children: [
                    config.baseUrl,
                    " · Key ",
                    config.apiKey === "" ? "未设置" : config.apiKey.slice(0, 8) + "…"
                  ] })
                ] })
              }
            ),
            /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", flexDirection: "column", gap: 10 }, children: [
              /* @__PURE__ */ jsxRuntimeExports.jsxs("label", { style: { display: "flex", flexDirection: "column", gap: 4 }, children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-cap", children: "Base URL" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  "input",
                  {
                    className: "input",
                    value: draft.baseUrl,
                    placeholder: "例如 https://api.example.com/v1",
                    onChange: (e) => setDraft({ ...draft, baseUrl: e.target.value })
                  }
                )
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("label", { style: { display: "flex", flexDirection: "column", gap: 4 }, children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-cap", children: "API Key" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  "input",
                  {
                    className: "input",
                    type: "password",
                    value: draft.apiKey,
                    placeholder: "sk-…",
                    onChange: (e) => setDraft({ ...draft, apiKey: e.target.value })
                  }
                )
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("label", { style: { display: "flex", flexDirection: "column", gap: 4 }, children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-cap", children: "模型名" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  "input",
                  {
                    className: "input",
                    value: draft.model,
                    placeholder: "例如 gpt-4o-mini / deepseek-chat",
                    onChange: (e) => setDraft({ ...draft, model: e.target.value })
                  }
                )
              ] })
            ] }),
            /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { marginTop: 14 }, children: /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn btn-primary", onClick: () => setChatOpen(true), children: "打开 Nova 对话" }) })
          ]
        }
      ),
      /* @__PURE__ */ jsxRuntimeExports.jsxs(SectionCard, { title: "游戏内辅助浮窗", children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", style: { marginBottom: 10 }, children: "启动游戏时自动弹出浮窗，游戏内随时查资料；与顶栏 Nova 共用同一份对话。" }),
        /* @__PURE__ */ jsxRuntimeExports.jsx(
          Toggle,
          {
            checked: overlay,
            onChange: (v2) => {
              setOverlay(v2);
              if (v2) push("info", "游戏内浮窗：内核能力接入后可用");
            },
            label: "游戏内辅助浮窗"
          }
        )
      ] })
    ] }),
    /* @__PURE__ */ jsxRuntimeExports.jsxs(Modal, { open: chatOpen, title: "Nova", onClose: () => setChatOpen(false), width: 720, children: [
      /* @__PURE__ */ jsxRuntimeExports.jsxs(
        "div",
        {
          ref: bodyRef,
          style: { maxHeight: "52vh", overflowY: "auto", display: "flex", flexDirection: "column", gap: 10 },
          children: [
            messages.length === 0 && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", style: { padding: "16px 0" }, children: configured ? "问点什么吧：查版本、算配置、写指令都可以。" : "先在「模型设置」里填好 Base URL 与模型名。" }),
            messages.map((m2) => /* @__PURE__ */ jsxRuntimeExports.jsxs(
              "div",
              {
                style: {
                  alignSelf: m2.role === "user" ? "flex-end" : "flex-start",
                  maxWidth: "78%",
                  padding: "8px 12px",
                  borderRadius: 12,
                  background: m2.role === "user" ? "var(--accent)" : "var(--surface-subtle)",
                  color: m2.role === "user" ? "var(--text-on-accent)" : "var(--text-primary)",
                  border: m2.role === "user" ? "none" : "1px solid var(--stroke-subtle)",
                  whiteSpace: "pre-wrap",
                  fontSize: 13,
                  animation: m2.streaming ? "pulse 1.5s infinite" : void 0
                },
                children: [
                  m2.tool !== void 0 && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "t-meta", style: { marginBottom: 4, opacity: 0.8 }, children: [
                    "正在调用：",
                    m2.tool
                  ] }),
                  m2.content,
                  m2.plan !== void 0 && m2.plan.length > 0 && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { marginTop: 8, display: "flex", flexDirection: "column", gap: 4 }, children: [
                    m2.plan.map((p2) => /* @__PURE__ */ jsxRuntimeExports.jsxs(
                      "div",
                      {
                        className: "t-meta",
                        style: { padding: "6px 8px", borderRadius: 8, background: "var(--surface-hover)" },
                        children: [
                          /* @__PURE__ */ jsxRuntimeExports.jsx("b", { children: p2.title }),
                          p2.detail !== "" ? ` — ${p2.detail}` : ""
                        ]
                      },
                      p2.title
                    )),
                    plan !== null && plan.items === m2.plan && /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 8, marginTop: 4 }, children: [
                      /* @__PURE__ */ jsxRuntimeExports.jsx(
                        "button",
                        {
                          className: "btn btn-primary btn-sm",
                          disabled: planBusy,
                          onClick: async () => {
                            setPlanBusy(true);
                            try {
                              const r2 = await request(
                                "nova.plan.execute",
                                { id: plan.planId }
                              );
                              push("success", r2.message ?? "已应用");
                              setPlan(null);
                            } catch (e) {
                              push("error", String(e));
                            } finally {
                              setPlanBusy(false);
                            }
                          },
                          children: planBusy ? "执行中…" : "执行"
                        }
                      ),
                      /* @__PURE__ */ jsxRuntimeExports.jsx(
                        "button",
                        {
                          className: "btn btn-sm",
                          disabled: planBusy,
                          onClick: async () => {
                            try {
                              await request("nova.plan.discard", { id: plan.planId });
                            } catch {
                            }
                            setPlan(null);
                          },
                          children: "放弃"
                        }
                      )
                    ] })
                  ] })
                ]
              },
              m2.id
            ))
          ]
        }
      ),
      /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 8, marginTop: 12 }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx(
          "input",
          {
            className: "input",
            style: { flex: 1 },
            value: input,
            placeholder: "给 Nova 发消息…",
            onChange: (e) => setInput(e.target.value),
            onKeyDown: (e) => {
              if (e.key === "Enter" && !e.shiftKey) void send();
            }
          }
        ),
        /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn btn-primary", disabled: busy || input.trim() === "", onClick: () => void send(), children: busy ? "回复中…" : "发送" })
      ] })
    ] }),
    host
  ] });
}
const SECTIONS = [
  { id: "game", label: "游戏", icon: /* @__PURE__ */ jsxRuntimeExports.jsx(IconHome, { size: 15 }) },
  { id: "java", label: "Java", icon: /* @__PURE__ */ jsxRuntimeExports.jsx(IconCube, { size: 15 }) },
  { id: "download", label: "下载与资源", icon: /* @__PURE__ */ jsxRuntimeExports.jsx(IconImport, { size: 15 }) },
  { id: "general", label: "通用", icon: /* @__PURE__ */ jsxRuntimeExports.jsx(IconSettings, { size: 15 }) },
  { id: "crossdevice", label: "跨设备", icon: /* @__PURE__ */ jsxRuntimeExports.jsx(IconLink, { size: 15 }) }
];
function SectionTitle(props) {
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", alignItems: "center", gap: 10, marginBottom: 12 }, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx(
      "span",
      {
        style: { width: 4, height: 16, borderRadius: 2, background: "var(--accent)", flexShrink: 0 }
      }
    ),
    /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-strong", style: { fontSize: 15 }, children: props.children })
  ] });
}
function Field(props) {
  if (props.inline === true) {
    return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", alignItems: "center", gap: 12, marginBottom: 12 }, children: [
      /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minWidth: 0 }, children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-body", style: { color: "var(--text-primary)" }, children: props.label }),
        props.hint !== void 0 && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: props.hint })
      ] }),
      props.children
    ] });
  }
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { marginBottom: 14 }, children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-body", style: { color: "var(--text-primary)", marginBottom: 6 }, children: props.label }),
    props.children,
    props.hint !== void 0 && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", style: { marginTop: 6 }, children: props.hint })
  ] });
}
function Slider(props) {
  const pct = (props.value - props.min) / (props.max - props.min) * 100;
  return /* @__PURE__ */ jsxRuntimeExports.jsx(
    "input",
    {
      type: "range",
      min: props.min,
      max: props.max,
      step: props.step ?? 1,
      value: props.value,
      onChange: (e) => props.onChange(Number(e.target.value)),
      style: {
        width: "100%",
        "--fill": `${pct}%`
      }
    }
  );
}
const EMPTY = {
  gameDir: "",
  javaExecutable: "",
  minMemoryMb: 512,
  maxMemoryMb: 4096,
  downloadSource: "auto",
  downloadConcurrency: 16,
  downloadMaxRetries: 3,
  downloadCustomMirrorUrl: "",
  versionIsolated: true,
  useBuiltinJvmArgs: true,
  extraJvmArgs: [],
  language: "zh_CN",
  theme: "system",
  accountWalkAnimation: true,
  microsoftLoginEmbedded: true,
  curseforgeApiKey: "",
  windowWidth: 854,
  windowHeight: 480,
  fullscreen: false,
  globalEnvVars: {},
  launchAutoRetry: true,
  showGameLogOnLaunch: false,
  novaOverlayEnabled: false,
  resourceCacheEnabled: true,
  motionLevel: "",
  backgroundMode: "none",
  backgroundBuiltinId: "",
  backgroundImagePath: "",
  backgroundImageOpacity: 0.7,
  backgroundBlurRadius: 24,
  backgroundMaskOpacity: 0,
  backgroundFollowTheme: true,
  deviceLinkEnabled: false,
  deviceName: "",
  p2pDownloadEnabled: true,
  autoUpdateEnabled: true,
  updateManifestUrl: ""
};
function SettingsPage(props) {
  const { push, host } = useToasts();
  const [section, setSection] = reactExports.useState("game");
  const [loading, setLoading] = reactExports.useState(true);
  const [saving, setSaving] = reactExports.useState(false);
  const [s, setS] = reactExports.useState(EMPTY);
  const [jvmArgsText, setJvmArgsText] = reactExports.useState("");
  const [javaProbe, setJavaProbe] = reactExports.useState("");
  const load = reactExports.useCallback(async () => {
    setLoading(true);
    try {
      const snap = await request("settings.get");
      setS({ ...EMPTY, ...snap });
      setJvmArgsText((snap.extraJvmArgs ?? []).join("\n"));
    } catch (e) {
      push("error", `读取设置失败：${String(e)}`);
    }
    setLoading(false);
  }, [push]);
  reactExports.useEffect(() => {
    void load();
  }, [load]);
  const set = (key, value) => setS((prev) => {
    const next = { ...prev, [key]: value };
    applyVisualSettings(next);
    return next;
  });
  async function probeJava() {
    setJavaProbe("探测中…");
    try {
      const r2 = await request("java.list");
      setJavaProbe(
        r2.runtimes.length === 0 ? "未找到可用的 Java —— 请安装 JRE 17+ 或在上方指定路径" : "已发现：" + r2.runtimes.map((j) => `${j.versionString}（Java ${j.majorVersion}）`).join("、")
      );
    } catch (e) {
      setJavaProbe(`探测失败：${String(e)}`);
    }
  }
  async function save() {
    setSaving(true);
    try {
      const snapshot = await request("settings.apply", {
        gameDir: s.gameDir,
        javaExecutable: s.javaExecutable,
        minMemoryMb: s.minMemoryMb,
        maxMemoryMb: s.maxMemoryMb,
        downloadSource: s.downloadSource,
        downloadConcurrency: s.downloadConcurrency,
        downloadMaxRetries: s.downloadMaxRetries,
        downloadCustomMirrorUrl: s.downloadCustomMirrorUrl,
        versionIsolated: s.versionIsolated,
        useBuiltinJvmArgs: s.useBuiltinJvmArgs,
        extraJvmArgs: jvmArgsText.split("\n").map((x2) => x2.trim()).filter((x2) => x2 !== ""),
        language: s.language,
        theme: props.theme.mode,
        accountWalkAnimation: s.accountWalkAnimation,
        microsoftLoginEmbedded: s.microsoftLoginEmbedded,
        curseforgeApiKey: s.curseforgeApiKey,
        windowWidth: s.windowWidth,
        windowHeight: s.windowHeight,
        fullscreen: s.fullscreen,
        globalEnvVars: s.globalEnvVars,
        launchAutoRetry: s.launchAutoRetry,
        showGameLogOnLaunch: s.showGameLogOnLaunch,
        novaOverlayEnabled: s.novaOverlayEnabled,
        resourceCacheEnabled: s.resourceCacheEnabled,
        motionLevel: s.motionLevel,
        backgroundMode: s.backgroundMode,
        backgroundBuiltinId: s.backgroundBuiltinId,
        backgroundImagePath: s.backgroundImagePath,
        backgroundImageOpacity: s.backgroundImageOpacity,
        backgroundBlurRadius: s.backgroundBlurRadius,
        backgroundMaskOpacity: s.backgroundMaskOpacity,
        backgroundFollowTheme: s.backgroundFollowTheme,
        deviceLinkEnabled: s.deviceLinkEnabled,
        deviceName: s.deviceName,
        p2pDownloadEnabled: s.p2pDownloadEnabled,
        autoUpdateEnabled: s.autoUpdateEnabled,
        updateManifestUrl: s.updateManifestUrl
      });
      setS({ ...EMPTY, ...snapshot });
      push("success", "设置已保存");
    } catch (e) {
      push("error", `保存失败：${String(e)}`);
    } finally {
      setSaving(false);
    }
  }
  const envEntries = Object.entries(s.globalEnvVars);
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page", children: [
    /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { flex: 1, minHeight: 0, display: "flex", gap: 16 }, children: [
      /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "card", style: { width: 230, flexShrink: 0, padding: 8, alignSelf: "flex-start" }, children: SECTIONS.map((item) => /* @__PURE__ */ jsxRuntimeExports.jsxs(
        "button",
        {
          className: `nav-item${section === item.id ? " nav-active" : ""}`,
          style: { height: 40, width: "100%" },
          onClick: () => setSection(item.id),
          children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "nav-icon", style: { fontFamily: "inherit" }, children: item.icon }),
            /* @__PURE__ */ jsxRuntimeExports.jsx("span", { children: item.label })
          ]
        },
        item.id
      )) }),
      /* @__PURE__ */ jsxRuntimeExports.jsxs(
        "div",
        {
          style: {
            flex: 1,
            minWidth: 0,
            overflowY: "auto",
            display: "flex",
            flexDirection: "column",
            gap: 14
          },
          children: [
            loading && /* @__PURE__ */ jsxRuntimeExports.jsx(SkeletonList, { rows: 3 }),
            !loading && section === "game" && /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(SectionTitle, { children: "游戏目录" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(Field, { label: ".minecraft 目录", hint: "版本、资源与库文件将安装到该目录", children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 8 }, children: [
                  /* @__PURE__ */ jsxRuntimeExports.jsx(
                    "input",
                    {
                      className: "input",
                      style: { flex: 1 },
                      value: s.gameDir,
                      onChange: (e) => set("gameDir", e.target.value)
                    }
                  ),
                  /* @__PURE__ */ jsxRuntimeExports.jsx(
                    "button",
                    {
                      className: "btn",
                      title: "打开目录",
                      onClick: () => void request("shell.openPath", { path: s.gameDir }).catch(
                        (e) => push("error", String(e))
                      ),
                      children: /* @__PURE__ */ jsxRuntimeExports.jsx(IconFolder, { size: 14 })
                    }
                  )
                ] }) })
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(SectionTitle, { children: "内存分配" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(Field, { label: `最大内存（${Math.round(s.maxMemoryMb / 1024)} GB）`, children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Slider,
                  {
                    min: 1,
                    max: 16,
                    value: Math.max(1, Math.round(s.maxMemoryMb / 1024)),
                    onChange: (v2) => set("maxMemoryMb", v2 * 1024)
                  }
                ) }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(Field, { label: `最小内存（${s.minMemoryMb} MB）`, children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Slider,
                  {
                    min: 256,
                    max: 4096,
                    step: 256,
                    value: s.minMemoryMb,
                    onChange: (v2) => set("minMemoryMb", v2)
                  }
                ) }),
                /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: "内存并非越大越流畅；整合包一般 4–8 GB 足够。" })
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(SectionTitle, { children: "启动可靠性" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Field,
                  {
                    label: "启动失败自动重试一次",
                    hint: "游戏启动后 8 秒内异常退出时，自动重试一次（不重复环境体检）",
                    inline: true,
                    children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                      Toggle,
                      {
                        checked: s.launchAutoRetry,
                        onChange: (v2) => set("launchAutoRetry", v2),
                        label: "自动重试"
                      }
                    )
                  }
                ),
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Field,
                  {
                    label: "启动页默认展开实时日志",
                    hint: "启动时在启动页显示游戏实时输出，便于排查启动问题",
                    inline: true,
                    children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                      Toggle,
                      {
                        checked: s.showGameLogOnLaunch,
                        onChange: (v2) => set("showGameLogOnLaunch", v2),
                        label: "展开实时日志"
                      }
                    )
                  }
                )
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(SectionTitle, { children: "默认窗口" }),
                /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 12, alignItems: "flex-end" }, children: [
                  /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { width: 160 }, children: /* @__PURE__ */ jsxRuntimeExports.jsx(Field, { label: "窗口宽度", children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                    "input",
                    {
                      className: "input",
                      style: { width: "100%" },
                      value: `${s.windowWidth}`,
                      onChange: (e) => set("windowWidth", Number(e.target.value) || 854)
                    }
                  ) }) }),
                  /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { width: 160 }, children: /* @__PURE__ */ jsxRuntimeExports.jsx(Field, { label: "窗口高度", children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                    "input",
                    {
                      className: "input",
                      style: { width: "100%" },
                      value: `${s.windowHeight}`,
                      onChange: (e) => set("windowHeight", Number(e.target.value) || 480)
                    }
                  ) }) }),
                  /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { paddingBottom: 14 }, children: [
                    /* @__PURE__ */ jsxRuntimeExports.jsx(
                      Toggle,
                      {
                        checked: s.fullscreen,
                        onChange: (v2) => set("fullscreen", v2),
                        label: "全屏启动"
                      }
                    ),
                    /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-body", style: { marginLeft: 8 }, children: "全屏启动" })
                  ] })
                ] })
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(SectionTitle, { children: "启动参数" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(Field, { label: "额外 JVM 参数（每行一条）", hint: "例如 -XX:+UseG1GC", children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                  "textarea",
                  {
                    className: "input",
                    style: { width: "100%", minHeight: 90, fontFamily: "var(--font-mono)" },
                    value: jvmArgsText,
                    onChange: (e) => setJvmArgsText(e.target.value)
                  }
                ) }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Field,
                  {
                    label: "内置 JVM 参数",
                    hint: "关闭后将不再自动附加官方推荐 JVM 参数",
                    inline: true,
                    children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                      Toggle,
                      {
                        checked: s.useBuiltinJvmArgs,
                        onChange: (v2) => set("useBuiltinJvmArgs", v2),
                        label: "内置 JVM 参数"
                      }
                    )
                  }
                )
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(SectionTitle, { children: "全局环境变量" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", style: { marginBottom: 10 }, children: "对所有版本生效；版本级同名变量会覆盖此处。" }),
                envEntries.length === 0 && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: "还没有环境变量。" }),
                envEntries.map(([key, value]) => /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 8, marginBottom: 8 }, children: [
                  /* @__PURE__ */ jsxRuntimeExports.jsx("input", { className: "input", style: { width: 180 }, value: key, readOnly: true }),
                  /* @__PURE__ */ jsxRuntimeExports.jsx(
                    "input",
                    {
                      className: "input",
                      style: { flex: 1 },
                      value,
                      onChange: (e) => set("globalEnvVars", { ...s.globalEnvVars, [key]: e.target.value })
                    }
                  ),
                  /* @__PURE__ */ jsxRuntimeExports.jsx(
                    "button",
                    {
                      className: "btn btn-subtle btn-danger",
                      onClick: () => {
                        const next = { ...s.globalEnvVars };
                        delete next[key];
                        set("globalEnvVars", next);
                      },
                      children: /* @__PURE__ */ jsxRuntimeExports.jsx(IconClose, { size: 14 })
                    }
                  )
                ] }, key)),
                /* @__PURE__ */ jsxRuntimeExports.jsxs(
                  "button",
                  {
                    className: "btn",
                    onClick: () => {
                      const name = `VAR_${envEntries.length + 1}`;
                      set("globalEnvVars", { ...s.globalEnvVars, [name]: "" });
                    },
                    children: [
                      /* @__PURE__ */ jsxRuntimeExports.jsx(IconPlus, { size: 14 }),
                      " 添加环境变量"
                    ]
                  }
                )
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(SectionTitle, { children: "版本隔离（默认）" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", style: { marginBottom: 10 }, children: "新版本的默认隔离方式。开启后只隔离 mods/存档/配置/资源包/光影， 库与资源文件仍共享；单个版本可在详情页覆盖。" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Segmented,
                  {
                    value: s.versionIsolated ? "isolated" : "shared",
                    options: [
                      { value: "isolated", label: "版本隔离（推荐）" },
                      { value: "shared", label: "共享目录" }
                    ],
                    onChange: (v2) => set("versionIsolated", v2 === "isolated")
                  }
                )
              ] })
            ] }),
            !loading && section === "java" && /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(SectionTitle, { children: "Java" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Field,
                  {
                    label: "Java 可执行文件路径",
                    hint: "留空 = 自动探测；进入本页可点「搜索 Java」扫描本机运行时",
                    children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 8 }, children: [
                      /* @__PURE__ */ jsxRuntimeExports.jsx(
                        "input",
                        {
                          className: "input",
                          style: { flex: 1 },
                          value: s.javaExecutable,
                          placeholder: "例如 /usr/lib/jvm/java-17-openjdk/bin/java",
                          onChange: (e) => set("javaExecutable", e.target.value)
                        }
                      ),
                      /* @__PURE__ */ jsxRuntimeExports.jsxs("button", { className: "btn", onClick: () => void probeJava(), children: [
                        /* @__PURE__ */ jsxRuntimeExports.jsx(IconRefresh, { size: 14 }),
                        " 搜索 Java"
                      ] })
                    ] })
                  }
                ),
                javaProbe !== "" && /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", children: javaProbe })
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(SectionTitle, { children: "下载 Java" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "t-cap", style: { marginBottom: 10 }, children: "Temurin 运行时下载由内核提供；当前版本请在系统里安装 JRE 17/21。" }),
                /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 8, flexWrap: "wrap" }, children: [
                  /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", disabled: true, children: "下载 Java 21（Temurin）" }),
                  /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", disabled: true, children: "下载 Java 8（Temurin）" }),
                  /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", disabled: true, children: "删除选中运行时" })
                ] })
              ] })
            ] }),
            !loading && section === "download" && /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(SectionTitle, { children: "下载" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(Field, { label: `下载并发数（${s.downloadConcurrency}）`, hint: "1–64，过高可能被镜像限速", children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Slider,
                  {
                    min: 1,
                    max: 64,
                    value: s.downloadConcurrency,
                    onChange: (v2) => set("downloadConcurrency", v2)
                  }
                ) }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(Field, { label: `失败重试次数（${s.downloadMaxRetries}）`, children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Slider,
                  {
                    min: 0,
                    max: 10,
                    value: s.downloadMaxRetries,
                    onChange: (v2) => set("downloadMaxRetries", v2)
                  }
                ) }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(Field, { label: "下载源", hint: "自动模式会并发测速，选最快的源下载同一个文件", children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Select,
                  {
                    width: 360,
                    value: s.downloadSource,
                    options: [
                      { value: "auto", label: "自动（多源聚合）" },
                      { value: "official", label: "仅官方" },
                      { value: "bmclapi", label: "BMCLAPI" },
                      { value: "fastmirror", label: "FastMinecraftMirror" },
                      { value: "rainyun", label: "RainYun" },
                      { value: "custom", label: "自定义镜像地址" }
                    ],
                    onChange: (v2) => set("downloadSource", v2)
                  }
                ) }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(Field, { label: "自定义镜像根 URL", hint: "留空 = 不启用", children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                  "input",
                  {
                    className: "input",
                    style: { width: "100%" },
                    value: s.downloadCustomMirrorUrl,
                    placeholder: "https://mirror.example.com/",
                    onChange: (e) => set("downloadCustomMirrorUrl", e.target.value)
                  }
                ) }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Field,
                  {
                    label: "CurseForge API Key（可空）",
                    hint: "留空走社区只读代理；填了就直连官方 api.curseforge.com",
                    children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                      "input",
                      {
                        className: "input",
                        style: { width: "100%" },
                        value: s.curseforgeApiKey,
                        onChange: (e) => set("curseforgeApiKey", e.target.value)
                      }
                    )
                  }
                )
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(SectionTitle, { children: "资源列表与图标缓存" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Field,
                  {
                    label: "启用资源列表拉取缓存",
                    hint: "打开资源页时先用缓存瞬时展示，后台拉到新内容后替换；关闭后完全不读不写缓存",
                    inline: true,
                    children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                      Toggle,
                      {
                        checked: s.resourceCacheEnabled,
                        onChange: (v2) => set("resourceCacheEnabled", v2),
                        label: "资源缓存"
                      }
                    )
                  }
                ),
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  "button",
                  {
                    className: "btn",
                    onClick: () => push("info", "清空缓存：内核能力接入后可用"),
                    children: "清空缓存"
                  }
                )
              ] })
            ] }),
            !loading && section === "general" && /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(SectionTitle, { children: "主题" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(Field, { label: "主题", hint: "跟随系统时会随操作系统亮暗自动切换", children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Segmented,
                  {
                    value: props.theme.mode,
                    options: [
                      { value: "system", label: "跟随系统" },
                      { value: "light", label: "浅色" },
                      { value: "dark", label: "深色" }
                    ],
                    onChange: props.theme.setMode
                  }
                ) }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(Field, { label: "界面语言", children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Select,
                  {
                    width: 240,
                    value: s.language,
                    options: [
                      { value: "zh_CN", label: "简体中文" },
                      { value: "en_US", label: "English" }
                    ],
                    onChange: (v2) => set("language", v2)
                  }
                ) })
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(SectionTitle, { children: "性能与动效" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Field,
                  {
                    label: "动效档位",
                    hint: "极少：只保留页面转场；较少：+ 悬停反馈；均衡：+ 跟手光斑；全部：+ 弹簧形变",
                    children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                      Segmented,
                      {
                        value: s.motionLevel === "" ? "balanced" : s.motionLevel,
                        options: [
                          { value: "minimal", label: "极少" },
                          { value: "lite", label: "较少" },
                          { value: "balanced", label: "均衡" },
                          { value: "full", label: "全部" }
                        ],
                        onChange: (v2) => set("motionLevel", v2)
                      }
                    )
                  }
                )
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(SectionTitle, { children: "背景与外观" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(Field, { label: "背景", children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Select,
                  {
                    width: 280,
                    value: s.backgroundMode,
                    options: [
                      { value: "none", label: "关闭（纯毛玻璃）" },
                      { value: "builtin", label: "内置壁纸" },
                      { value: "image", label: "本地图片" }
                    ],
                    onChange: (v2) => set("backgroundMode", v2)
                  }
                ) }),
                s.backgroundMode === "image" && /* @__PURE__ */ jsxRuntimeExports.jsx(Field, { label: "图片文件", hint: "填本地图片的绝对路径", children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                  "input",
                  {
                    className: "input",
                    style: { width: "100%" },
                    value: s.backgroundImagePath,
                    placeholder: "/home/user/Pictures/bg.png",
                    onChange: (e) => set("backgroundImagePath", e.target.value)
                  }
                ) }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(Field, { label: `模糊强度（${Math.round(s.backgroundBlurRadius)}）`, children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Slider,
                  {
                    min: 0,
                    max: 64,
                    value: Math.round(s.backgroundBlurRadius),
                    onChange: (v2) => set("backgroundBlurRadius", v2)
                  }
                ) }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(Field, { label: `不透明度（${Math.round(s.backgroundImageOpacity * 100)}%）`, children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Slider,
                  {
                    min: 0,
                    max: 100,
                    value: Math.round(s.backgroundImageOpacity * 100),
                    onChange: (v2) => set("backgroundImageOpacity", v2 / 100)
                  }
                ) }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Field,
                  {
                    label: "背景跟随主题",
                    hint: "深色主题下自动压暗背景",
                    inline: true,
                    children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                      Toggle,
                      {
                        checked: s.backgroundFollowTheme,
                        onChange: (v2) => set("backgroundFollowTheme", v2),
                        label: "背景跟随主题"
                      }
                    )
                  }
                )
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(SectionTitle, { children: "账户与登录" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Field,
                  {
                    label: "账户页走路动画",
                    hint: "关闭后，账户页右侧的玩家形象将保持站立姿势",
                    inline: true,
                    children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                      Toggle,
                      {
                        checked: s.accountWalkAnimation,
                        onChange: (v2) => set("accountWalkAnimation", v2),
                        label: "走路动画"
                      }
                    )
                  }
                ),
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Field,
                  {
                    label: "使用内置登录窗口",
                    hint: "微软登录在启动器内完成；关闭后改为显示验证码并打开浏览器",
                    inline: true,
                    children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                      Toggle,
                      {
                        checked: s.microsoftLoginEmbedded,
                        onChange: (v2) => set("microsoftLoginEmbedded", v2),
                        label: "内置登录窗口"
                      }
                    )
                  }
                )
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(SectionTitle, { children: "Nova 游戏内浮窗" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Field,
                  {
                    label: "显示 Nova 浮窗",
                    hint: "游戏运行时在屏幕角落显示浮窗，Ctrl+N 随时呼出对话；下局生效",
                    inline: true,
                    children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                      Toggle,
                      {
                        checked: s.novaOverlayEnabled,
                        onChange: (v2) => set("novaOverlayEnabled", v2),
                        label: "Nova 浮窗"
                      }
                    )
                  }
                )
              ] }),
              /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", children: [
                /* @__PURE__ */ jsxRuntimeExports.jsx(SectionTitle, { children: "软件更新" }),
                /* @__PURE__ */ jsxRuntimeExports.jsx(
                  Field,
                  {
                    label: "自动检查更新",
                    hint: "启动几秒后后台检查新版本，发现时询问；游戏运行期间不打扰",
                    inline: true,
                    children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                      Toggle,
                      {
                        checked: s.autoUpdateEnabled,
                        onChange: (v2) => set("autoUpdateEnabled", v2),
                        label: "自动检查更新"
                      }
                    )
                  }
                ),
                /* @__PURE__ */ jsxRuntimeExports.jsx("button", { className: "btn", onClick: () => push("info", "检查更新：内核能力接入后可用"), children: "检查更新" })
              ] })
            ] }),
            !loading && section === "crossdevice" && /* @__PURE__ */ jsxRuntimeExports.jsx(jsxRuntimeExports.Fragment, { children: /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "card", children: [
              /* @__PURE__ */ jsxRuntimeExports.jsx(SectionTitle, { children: "跨设备与 P2P" }),
              /* @__PURE__ */ jsxRuntimeExports.jsx(
                Field,
                {
                  label: "跨设备存档同步",
                  hint: "同一 Starcloud ID 的设备在局域网内互相可见，可把世界与版本配置推送到其他设备",
                  inline: true,
                  children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                    Toggle,
                    {
                      checked: s.deviceLinkEnabled,
                      onChange: (v2) => set("deviceLinkEnabled", v2),
                      label: "跨设备存档同步"
                    }
                  )
                }
              ),
              /* @__PURE__ */ jsxRuntimeExports.jsx(Field, { label: "设备名称", hint: "留空 = 使用主机名", children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                "input",
                {
                  className: "input",
                  style: { width: 320 },
                  value: s.deviceName,
                  onChange: (e) => set("deviceName", e.target.value)
                }
              ) }),
              /* @__PURE__ */ jsxRuntimeExports.jsx(
                Field,
                {
                  label: "P2P 下载加速",
                  hint: "在局域网内从同账号设备下载已缓存的文件",
                  inline: true,
                  children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                    Toggle,
                    {
                      checked: s.p2pDownloadEnabled,
                      onChange: (v2) => set("p2pDownloadEnabled", v2),
                      label: "P2P 下载加速"
                    }
                  )
                }
              ),
              /* @__PURE__ */ jsxRuntimeExports.jsx(InfoBar, { kind: "info", title: "需要 Starcloud ID", children: "同步与 P2P 加速需要登录 Starcloud ID。" }),
              /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { marginTop: 10 }, children: /* @__PURE__ */ jsxRuntimeExports.jsx(
                "button",
                {
                  className: "btn btn-primary",
                  onClick: () => push("info", "Starcloud ID 登录：内核能力接入后可用"),
                  children: "去登录"
                }
              ) })
            ] }) })
          ]
        }
      )
    ] }),
    /* @__PURE__ */ jsxRuntimeExports.jsxs(
      "div",
      {
        style: {
          display: "flex",
          alignItems: "center",
          gap: 12,
          marginTop: 14,
          padding: "10px 14px",
          borderRadius: "var(--r-card)",
          background: "var(--surface-subtle)",
          border: "1px solid var(--stroke-subtle)",
          flexShrink: 0
        },
        children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "t-cap", style: { flex: 1 }, children: "修改后点右侧保存；未保存的改动会在切换分类时保留。" }),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "btn btn-primary",
              disabled: saving || loading,
              onClick: () => void save(),
              children: saving ? "保存中…" : "保存设置"
            }
          )
        ]
      }
    ),
    host
  ] });
}
function stamp(d) {
  const p2 = (n2, w2 = 2) => n2.toString().padStart(w2, "0");
  return `${d.getFullYear()}-${p2(d.getMonth() + 1)}-${p2(d.getDate())} ${p2(
    d.getHours()
  )}:${p2(d.getMinutes())}:${p2(d.getSeconds())}.${p2(d.getMilliseconds(), 3)}`;
}
function LogsPage(props) {
  const { push, host } = useToasts();
  const [tab, setTab] = reactExports.useState("launcher");
  const [launcherLines, setLauncherLines] = reactExports.useState([]);
  const [gameLines, setGameLines] = reactExports.useState([]);
  const seq = reactExports.useRef(1);
  reactExports.useEffect(() => {
    let alive = true;
    onBridgeEvent((event, params) => {
      if (!alive || event === "game.output") return;
      setLauncherLines((prev) => [
        ...prev.slice(-499),
        {
          id: seq.current++,
          at: stamp(/* @__PURE__ */ new Date()),
          channel: event.split(".")[0] ?? "app",
          text: `${event} ${JSON.stringify(params).slice(0, 200)}`
        }
      ]);
    });
    return () => {
      alive = false;
    };
  }, []);
  const lastGameCount = reactExports.useRef(0);
  reactExports.useEffect(() => {
    const incoming = props.session.logs;
    if (incoming.length === lastGameCount.current) return;
    const added = incoming.slice(lastGameCount.current);
    lastGameCount.current = incoming.length;
    const now2 = /* @__PURE__ */ new Date();
    setGameLines((prev) => [
      ...prev.slice(-999),
      ...added.map((line) => ({
        id: seq.current++,
        at: stamp(now2),
        channel: "game",
        text: line
      }))
    ]);
  }, [props.session.logs]);
  const lines = tab === "launcher" ? launcherLines : gameLines;
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page", children: [
    /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "page-head", children: [
      [
        { id: "launcher", label: "启动器日志" },
        { id: "game", label: "游戏日志" }
      ].map((t2) => /* @__PURE__ */ jsxRuntimeExports.jsxs(
        "button",
        {
          className: "btn btn-subtle",
          style: { display: "flex", alignItems: "center", gap: 8 },
          onClick: () => setTab(t2.id),
          children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx(
              "span",
              {
                style: {
                  width: 16,
                  height: 16,
                  borderRadius: "50%",
                  border: tab === t2.id ? "5px solid var(--accent)" : "1.5px solid var(--stroke-strong)",
                  background: "transparent",
                  flexShrink: 0
                }
              }
            ),
            /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: tab === t2.id ? "t-strong" : "t-body", children: t2.label })
          ]
        },
        t2.id
      )),
      /* @__PURE__ */ jsxRuntimeExports.jsx(
        "button",
        {
          className: "btn",
          onClick: () => push("info", "打开日志目录：主进程 openPath 接入后可用"),
          children: "打开日志目录"
        }
      ),
      /* @__PURE__ */ jsxRuntimeExports.jsx("div", { style: { flex: 1 } }),
      /* @__PURE__ */ jsxRuntimeExports.jsxs(
        "button",
        {
          className: "btn btn-subtle btn-sm",
          onClick: () => {
            if (tab === "launcher") setLauncherLines([]);
            else setGameLines([]);
          },
          children: [
            /* @__PURE__ */ jsxRuntimeExports.jsx(IconClose, { size: 13 }),
            " 清空"
          ]
        }
      )
    ] }),
    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "card card-flush", style: { flex: 1, minHeight: 0 }, children: /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "log", children: lines.length === 0 ? /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "empty", style: { minHeight: 120 }, children: /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "empty-hint", children: tab === "launcher" ? "暂无启动器日志。切换页面或触发桥接事件后会记录在这里。" : "暂无游戏日志。启动游戏后这里会显示输出。" }) }) : lines.map((l2) => /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { style: { display: "flex", gap: 8 }, children: [
      /* @__PURE__ */ jsxRuntimeExports.jsxs("span", { style: { opacity: 0.55, flexShrink: 0 }, children: [
        "[",
        l2.at,
        "]"
      ] }),
      /* @__PURE__ */ jsxRuntimeExports.jsxs("span", { style: { opacity: 0.7, flexShrink: 0, width: 54 }, children: [
        "[",
        l2.channel,
        "]"
      ] }),
      /* @__PURE__ */ jsxRuntimeExports.jsx("span", { style: { minWidth: 0, wordBreak: "break-word" }, children: l2.text })
    ] }, l2.id)) }) }),
    host
  ] });
}
function App(props) {
  const theme = useTheme(props.initialTheme);
  const [page, setPage] = reactExports.useState("home");
  const session = useGameSession();
  useTaskCenter();
  const [tasksOpen, setTasksOpen] = reactExports.useState(false);
  reactExports.useEffect(() => {
    void loadAndApplyVisualSettings();
  }, []);
  const [appVersion$1, setAppVersion] = reactExports.useState("");
  reactExports.useEffect(() => {
    void appVersion().then(setAppVersion).catch(() => void 0);
  }, []);
  const running = session.pid !== void 0 && session.pid > 0;
  reactExports.useEffect(() => {
    const onKey = (e) => {
      if (!e.ctrlKey && !e.metaKey) return;
      const order = [
        "home",
        "versions",
        "download",
        "saves",
        "multiplayer",
        "lab",
        "projections",
        "running",
        "accounts",
        "plugins"
      ];
      if (e.key >= "1" && e.key <= "9") {
        const target = order[Number(e.key) - 1];
        if (target !== void 0) {
          e.preventDefault();
          setPage(target);
        }
      } else if (e.key === "0") {
        e.preventDefault();
        setPage("settings");
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);
  const render = () => {
    switch (page) {
      case "home":
        return /* @__PURE__ */ jsxRuntimeExports.jsx(
          HomePage,
          {
            session,
            onNavigate: (target) => setPage(target)
          }
        );
      case "versions":
        return /* @__PURE__ */ jsxRuntimeExports.jsx(
          VersionsPage,
          {
            onOpenDetail: (id2) => setPage("versions"),
            onOpenSettings: () => setPage("settings")
          }
        );
      case "download":
        return /* @__PURE__ */ jsxRuntimeExports.jsx(DownloadPage, {});
      case "saves":
        return /* @__PURE__ */ jsxRuntimeExports.jsx(SavesPage, {});
      case "multiplayer":
        return /* @__PURE__ */ jsxRuntimeExports.jsx(MultiplayerPage, {});
      case "lab":
        return /* @__PURE__ */ jsxRuntimeExports.jsx(LabPage, {});
      case "projections":
        return /* @__PURE__ */ jsxRuntimeExports.jsx(ProjectionsPage, {});
      case "running":
        return /* @__PURE__ */ jsxRuntimeExports.jsx(RunningPage, { session, onGoHome: () => setPage("home") });
      case "accounts":
        return /* @__PURE__ */ jsxRuntimeExports.jsx(AccountsPage, {});
      case "plugins":
        return /* @__PURE__ */ jsxRuntimeExports.jsx(PluginsPage, {});
      case "nova":
        return /* @__PURE__ */ jsxRuntimeExports.jsx(NovaPage, {});
      case "logs":
        return /* @__PURE__ */ jsxRuntimeExports.jsx(LogsPage, { session });
      case "settings":
        return /* @__PURE__ */ jsxRuntimeExports.jsx(SettingsPage, { theme });
      default:
        return /* @__PURE__ */ jsxRuntimeExports.jsx(HomePage, { session, onNavigate: (t2) => setPage(t2) });
    }
  };
  return /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "win mica", children: [
    /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "bg-layer", "aria-hidden": true }),
    /* @__PURE__ */ jsxRuntimeExports.jsxs(
      TitleBar,
      {
        title: PAGE_LABEL[page],
        themeMode: theme.mode,
        onCycleTheme: theme.cycle,
        actions: /* @__PURE__ */ jsxRuntimeExports.jsxs(jsxRuntimeExports.Fragment, { children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "tb-action-btn",
              title: "Nova 助手",
              "aria-label": "Nova 助手",
              onClick: () => setPage("nova"),
              children: /* @__PURE__ */ jsxRuntimeExports.jsx("img", { src: brandMark, alt: "", width: 16, height: 16 })
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "tb-action-btn",
              title: "调度中心",
              "aria-label": "调度中心",
              onClick: () => setTasksOpen(true),
              children: /* @__PURE__ */ jsxRuntimeExports.jsx(IconTasks, { size: 16 })
            }
          ),
          /* @__PURE__ */ jsxRuntimeExports.jsx(
            "button",
            {
              className: "tb-action-btn",
              title: "账户",
              "aria-label": "账户",
              onClick: () => setPage("accounts"),
              children: /* @__PURE__ */ jsxRuntimeExports.jsx(IconUser, { size: 16 })
            }
          )
        ] }),
        children: [
          /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "tb-brand", children: /* @__PURE__ */ jsxRuntimeExports.jsx("img", { src: brandMark, alt: "", width: 18, height: 18 }) }),
          /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "tb-appname", children: "Starcloud" }),
          /* @__PURE__ */ jsxRuntimeExports.jsx("span", { className: "tb-ver", children: appVersion$1 !== "" ? `开发版 V${appVersion$1}` : "Linux" })
        ]
      }
    ),
    /* @__PURE__ */ jsxRuntimeExports.jsxs("div", { className: "win-body", children: [
      /* @__PURE__ */ jsxRuntimeExports.jsx(Nav, { page, dark: theme.resolved === "dark", running, onNavigate: setPage }),
      /* @__PURE__ */ jsxRuntimeExports.jsxs("main", { className: "content", children: [
        /* @__PURE__ */ jsxRuntimeExports.jsx(
          LaunchOverlay,
          {
            session,
            versionId: session.launchVersion,
            onCancel: session.dismissLaunchError
          }
        ),
        /* @__PURE__ */ jsxRuntimeExports.jsx(ErrorBoundary, { label: PAGE_LABEL[page], children: /* @__PURE__ */ jsxRuntimeExports.jsx("div", { className: "page", children: render() }, page) }, page)
      ] })
    ] })
  ] });
}
installMockBridgeIfNeeded();
void resolveInitialTheme().then((initial) => {
  client.createRoot(document.getElementById("root")).render(
    /* @__PURE__ */ jsxRuntimeExports.jsx(React.StrictMode, { children: /* @__PURE__ */ jsxRuntimeExports.jsx(App, { initialTheme: initial }) })
  );
});
