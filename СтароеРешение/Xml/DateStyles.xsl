<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
	<xsl:output method="xml" version="1.0" encoding="UTF-8" indent="yes"/>
	<xsl:template name="FormatDateToDDMMYYY">
		<xsl:param name="Date"/>
		<xsl:value-of select="concat(substring($Date,9,2),'.',substring($Date,6,2),'.',substring($Date,1,4))"/>
	</xsl:template>
</xsl:stylesheet>
