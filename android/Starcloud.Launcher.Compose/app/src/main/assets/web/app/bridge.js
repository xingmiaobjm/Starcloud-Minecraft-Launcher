/**
 * Web UI ↔ 安卓原生 适配层。
 *
 * 目标：让同一份 React 页面在 Electron 与安卓 WebView 里跑，页面只认
 * `window.bridge.request(method, params)`（Electron 的 preload 也是这个形状）。
 *
 * 协议：
 *   JS  → 原生  starcloud.call(id, method, paramsJson)   （@JavascriptInterface，同步返回 void）
 *   原生 → JS   window.__sc.reply(id, resultJson)
 *              window.__sc.event(name, paramsJson)
 *
 * 注意：@JavascriptInterface 只能同步返回字符串，长任务（下载/安装）不能在这里等，
 * 所以一律走 id + 回调，Promise 由本层负责配对。
 */
(function () {
  'use strict'

  var seq = 0
  var pending = new Map()
  var listeners = []

  window.__sc = {
    reply: function (id, payloadJson) {
      var p = pending.get(id)
      if (!p) return
      pending.delete(id)
      var data
      try {
        data = JSON.parse(payloadJson)
      } catch (e) {
        p.reject(new Error('bad json from native: ' + e.message))
        return
      }
      if (data && typeof data === 'object' && data.error) {
        var err = new Error(data.error.message || data.error.code || 'native error')
        err.code = data.error.code
        p.reject(err)
      } else {
        p.resolve(data)
      }
    },
    event: function (name, payloadJson) {
      var params
      try {
        params = JSON.parse(payloadJson)
      } catch (e) {
        params = {}
      }
      listeners.forEach(function (h) {
        try {
          h(name, params)
        } catch (e) {
          /* 单个订阅者出错不影响其它 */
        }
      })
    },
  }

  var available = typeof window.starcloud !== 'undefined' &&
    typeof window.starcloud.call === 'function'

  /** 页面里的统一入口；浏览器里调试时没有原生，直接拒绝，调用方自己兜底 */
  var bridge = {
    request: function (method, params) {
      return new Promise(function (resolve, reject) {
        if (!available) {
          var e = new Error('当前环境没有原生桥（浏览器调试态）')
          e.code = 'no-bridge'
          reject(e)
          return
        }
        var id = 'r' + ++seq
        pending.set(id, { resolve: resolve, reject: reject })
        try {
          window.starcloud.call(id, method, JSON.stringify(params || {}))
        } catch (err) {
          pending.delete(id)
          reject(err)
        }
      })
    },
    appVersion: function () {
      return bridge.request('app.info').then(function (info) {
        return info.version
      })
    },
  }

  var bridgeEvents = {
    subscribe: function (handler) {
      listeners.push(handler)
      return function () {
        var i = listeners.indexOf(handler)
        if (i >= 0) listeners.splice(i, 1)
      }
    },
  }

  // 安卓没有系统主题查询接口，先跟系统深色偏好走
  var theme = {
    get: function () {
      var dark = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches
      return Promise.resolve({ dark: !!dark, source: 'system' })
    },
    onChange: function (handler) {
      if (!window.matchMedia) return
      var mq = window.matchMedia('(prefers-color-scheme: dark)')
      var fn = function (e) {
        handler({ dark: e.matches, source: 'system' })
      }
      if (mq.addEventListener) mq.addEventListener('change', fn)
    },
  }

  // 只在 WebView 里补齐：有原生桥才接管，没有就留给页面自己的 mock（浏览器调试）
  if (available && window.bridge === undefined) window.bridge = bridge
  if (window.bridgeEvents === undefined) window.bridgeEvents = bridgeEvents
  if (window.theme === undefined) window.theme = theme
})()
