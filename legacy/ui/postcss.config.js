export default {
  plugins: {
    "postcss-nesting": {},
    "postcss-preset-env": {
      features: {
        "nesting-rules": false,
      },
    },
  },
};