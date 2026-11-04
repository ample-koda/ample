// Where complaints about a share go. It is a setting of the server (AMPLE_REMIX_REPORT_EMAIL, sent with the limits), not part of the page:
// an instance without one has no complaints line, and the texts say that the operator of the site is the one to ask.
let ADDRESS = '';

export const setReportEmail = (address) => { ADDRESS = typeof address === 'string' ? address.trim() : ''; };
export const reportEmail = () => ADDRESS;

/** The mailto link of a share, or null when this instance has no address. */
export const reportLink = (shareId, origin) => {
  if (!ADDRESS) return null;
  const body = `Share ID: ${shareId}\nLink: ${origin}/mix/${shareId}\n\nWhat is the problem?\n`;
  return `mailto:${ADDRESS}?subject=${encodeURIComponent('AMPLE Remix share ' + shareId)}&body=${encodeURIComponent(body)}`;
};

/** Writes the address into every element marked data-report-email (when there is one; the others keep their generic text). */
export const showReportEmail = (root = document) => {
  if (!ADDRESS) return;
  for (const element of root.querySelectorAll('[data-report-email]')) element.textContent = ADDRESS;
};
