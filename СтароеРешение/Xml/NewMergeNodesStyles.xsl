<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:fo="http://www.w3.org/1999/XSL/Format" xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xmlns:xs='http://www.w3.org/2001/XMLSchema'>
	<xsl:variable name="SchemaDoc" select="document(tokenize(string(/*/@xsi:schemaLocation),' ')[2])"/>
	<xsl:template name="MergeNodes">
		<xsl:param name="OrigNode"/>
		<xsl:param name="RefNode"/>
		<xsl:variable name="Concated">
			<xsl:element name="{name($OrigNode)}" namespace="{namespace-uri($OrigNode)}">
				<xsl:copy-of select="$RefNode/@*"/>
				<xsl:copy-of select="$OrigNode/@*[name() != 'id' and name() != 'ref' and name() != 'uri']"/>
				<xsl:copy-of select="$OrigNode/*"/>
				<xsl:copy-of select="$RefNode/*"/>
			</xsl:element>
		</xsl:variable>
		<xsl:variable name="Cleaned">
			<xsl:element name="{name($OrigNode)}" namespace="{namespace-uri($OrigNode)}">
				<xsl:copy-of select="$Concated/*/@*"/>
				<xsl:for-each select="$Concated/*/*">
					<xsl:variable name="Name" select="name()"/>
					<xsl:variable name="NodeName" select="node-name(.)" as="xs:QName"/>
					<xsl:variable name="LocalName" select="string(local-name())"/>
					<xsl:if test="count(preceding-sibling::*[node-name(.) = $NodeName]) = 0 or $SchemaDoc//xs:element[@name = $LocalName and @maxOccurs != '1']">
						<xsl:copy-of select="."/>
					</xsl:if>
				</xsl:for-each>
			</xsl:element>
		</xsl:variable>
		<xsl:copy-of select="$Cleaned/*"/>
	</xsl:template>
</xsl:stylesheet>